using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // ═══════════════════════════════════════════════════════════════════════
    // PURCHASE RETURN — decreases stock (qty_out, trans_type "PUR_RETURN").
    // Mirror image of SalesReturnRepository: same global auto-increment
    // entry_no ("PR-{n}"), vendor fields instead of customer fields.
    // ═══════════════════════════════════════════════════════════════════════
    public partial class PurchaseReturnRepository
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new();
        private readonly StockMasterRepository _stockRepo = new();
        private readonly FinancialSyncService _financialSync = new();

        private const string ItemColumns = @"
            entry_no, sl_no, item_code, item_name, batch_id, batch_number, trans_id,
            qty, rate, discount_percent, discount_amount, taxable_amount,
            tax_percent, tax_amount, total_amount";

        // Standalone GetAll — no GenericRepository dependency required
        public List<PurchaseReturnSummary> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var list = db.Query<PurchaseReturnSummary>(
                    "SELECT * FROM purchase_return_summary ORDER BY entry_date DESC, entry_no DESC"
                ).ToList();

                foreach (var item in list)
                {
                    item.FormattedEntryNo = FormatEntryNo(item.EntryNo);
                }

                return list;
            }
        }

        public void InitializeDatabase()
        {
            lock (_initLock)
            {
                if (_initialized) return;
                _stockRepo.InitializeDatabase();

                using (IDbConnection db = DatabaseConfig.GetConnection())
                {
                    db.Execute(@"
                        CREATE TABLE IF NOT EXISTS purchase_return_summary (
                            entry_no            INTEGER PRIMARY KEY AUTOINCREMENT,
                            entry_date          TEXT NOT NULL,
                            original_bill_no    TEXT,
                            bill_type           TEXT NOT NULL,
                            vendor_code         TEXT,
                            vendor_name         TEXT,
                            vendor_address      TEXT,
                            vendor_phone        TEXT,
                            vendor_gst_no       TEXT,
                            state               TEXT,
                            state_code          TEXT,
                            gross_amount        REAL DEFAULT 0,
                            discount_amount     REAL DEFAULT 0,
                            discount_percent    REAL DEFAULT 0,
                            tax_amount          REAL DEFAULT 0,
                            round_off           REAL DEFAULT 0,
                            net_total           REAL DEFAULT 0,
                            to_cash_amount      REAL DEFAULT 0,
                            to_bank_amount      REAL DEFAULT 0,
                            bank_head_code      INTEGER DEFAULT 0,
                            bank_head_name      TEXT,
                            to_credit_amount    REAL DEFAULT 0,
                            is_cancelled        INTEGER DEFAULT 0,
                            accounts_synced     INTEGER NOT NULL DEFAULT 1,
                            created_by          TEXT,
                            created_date        TEXT,
                            last_modified_by    TEXT,
                            last_modified_date  TEXT
                        );

                        CREATE TABLE IF NOT EXISTS purchase_return_item_details (
                            entry_no          INTEGER NOT NULL,
                            sl_no             INTEGER NOT NULL,
                            item_code         TEXT NOT NULL,
                            item_name         TEXT,
                            batch_id          INTEGER NOT NULL,
                            batch_number      TEXT,
                            trans_id          INTEGER,
                            qty               REAL DEFAULT 0,
                            rate              REAL DEFAULT 0,
                            discount_percent  REAL DEFAULT 0,
                            discount_amount   REAL DEFAULT 0,
                            taxable_amount    REAL DEFAULT 0,
                            tax_percent       REAL DEFAULT 0,
                            tax_amount        REAL DEFAULT 0,
                            total_amount      REAL DEFAULT 0,
                            PRIMARY KEY (entry_no, sl_no)
                        );");

                    AccountsSyncStatus.EnsureColumn(db, "purchase_return_summary");
                }

                _initialized = true;
            }
        }

        public string FormatEntryNo(int entryNo) => $"PR-{entryNo}";

        /// <summary>Provisional next entry number for display only — see
        /// SalesReturnRepository.GetNextEntryNoPreview for the rationale.</summary>
        public int GetNextEntryNoPreview()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<int>(
                    "SELECT COALESCE(MAX(entry_no), 0) + 1 FROM purchase_return_summary");
            }
        }

        public bool OriginalBillExists(string formattedBillNo)
        {
            if (string.IsNullOrWhiteSpace(formattedBillNo)) return false;
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM purchase_summary_details WHERE bill_no = @No",
                    new { No = formattedBillNo }) > 0;
            }
        }

        /// <summary>Vendor-side batch lookup for the return entry combo.
        /// Only real, existing batches — a purchase return can't send back
        /// stock that was never purchased, so there's no synthetic "new
        /// batch" row here (unlike Sales Entry's negative-stock Path B).</summary>
        public List<PurchaseReturnItemLookup> GetReturnBatchLookups()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<PurchaseReturnItemLookup>(@"
                    SELECT
                        v.batch_id       AS BatchId,
                        v.item_code      AS ItemCode,
                        m.item_name      AS ItemName,
                        m.hsn_code       AS HsnCode,
                        v.batch_number   AS BatchNumber,
                        CAST(v.mrp             AS REAL) AS Mrp,
                        CAST(v.purchase_rate  AS REAL) AS PurchaseRate,
                        CAST(m.tax_rate       AS REAL) AS TaxRate,
                        CAST(v.current_qty    AS REAL) AS CurrentStock
                    FROM View_CurrentStock v
                    JOIN item_master m ON m.item_code = v.item_code
                    ORDER BY m.item_name, v.mrp").ToList();
            }
        }

        public List<AcHeadLookup> GetBankAcHeads() => new AccountHeadRepository().GetBankAcHeadLookups();
        public (int EntryNo, string Formatted) Save(PurchaseReturnSummary summary, List<PurchaseReturnItemDetail> items)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        db.Execute(@"
                            INSERT INTO purchase_return_summary
                                (entry_date, original_bill_no, bill_type, vendor_code, vendor_name,
                                 vendor_address, vendor_phone, vendor_gst_no, state, state_code,
                                 gross_amount, discount_amount, discount_percent, tax_amount, round_off, net_total,
                                 to_cash_amount, to_bank_amount, bank_head_code, bank_head_name, to_credit_amount,
                                 is_cancelled, created_by, created_date)
                            VALUES
                                (@EntryDate, @OriginalBillNo, @BillType, @VendorCode, @VendorName,
                                 @VendorAddress, @VendorPhone, @VendorGstNo, @State, @StateCode,
                                 @GrossAmount, @DiscountAmount, @DiscountPercent, @TaxAmount, @RoundOff, @NetTotal,
                                 @ToCashAmount, @ToBankAmount, @BankHeadCode, @BankHeadName, @ToCreditAmount,
                                 0, @CreatedBy, @CreatedDate)",
                            new
                            {
                                EntryDate = summary.EntryDate.ToString("yyyy-MM-dd"),
                                summary.OriginalBillNo,
                                summary.BillType,
                                summary.VendorCode,
                                summary.VendorName,
                                summary.VendorAddress,
                                summary.VendorPhone,
                                summary.VendorGstNo,
                                summary.State,
                                summary.StateCode,
                                summary.GrossAmount,
                                summary.DiscountAmount,
                                summary.DiscountPercent,
                                summary.TaxAmount,
                                summary.RoundOff,
                                summary.NetTotal,
                                summary.ToCashAmount,
                                summary.ToBankAmount,
                                summary.BankHeadCode,
                                summary.BankHeadName,
                                summary.ToCreditAmount,
                                summary.CreatedBy,
                                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                            }, tx);

                        int entryNo = db.ExecuteScalar<int>("SELECT last_insert_rowid()", transaction: tx);

                        InsertItemsAndStock(db, tx, entryNo, items);

                        tx.Commit();
                        summary.EntryNo = entryNo;
                        summary.FormattedEntryNo = FormatEntryNo(entryNo);
                    }
                    catch { tx.Rollback(); throw; }
                }
            }

            PostToAccounts(summary, items, summary.CreatedBy);
            return (summary.EntryNo, summary.FormattedEntryNo);
        }

        public void Update(PurchaseReturnSummary summary, List<PurchaseReturnItemDetail> items)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        var oldTransIds = db.Query<int>(
                            "SELECT trans_id FROM purchase_return_item_details WHERE entry_no = @EntryNo AND trans_id > 0",
                            new { EntryNo = summary.EntryNo }, tx);
                        foreach (var tid in oldTransIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, tid);

                        db.Execute("DELETE FROM purchase_return_item_details WHERE entry_no = @EntryNo",
                            new { EntryNo = summary.EntryNo }, tx);

                        db.Execute(@"
                            UPDATE purchase_return_summary SET
                                entry_date = @EntryDate, original_bill_no = @OriginalBillNo, bill_type = @BillType,
                                vendor_code = @VendorCode, vendor_name = @VendorName,
                                vendor_address = @VendorAddress, vendor_phone = @VendorPhone,
                                vendor_gst_no = @VendorGstNo, state = @State, state_code = @StateCode,
                                gross_amount = @GrossAmount, discount_amount = @DiscountAmount,
                                discount_percent = @DiscountPercent, tax_amount = @TaxAmount,
                                round_off = @RoundOff, net_total = @NetTotal,
                                to_cash_amount = @ToCashAmount, to_bank_amount = @ToBankAmount,
                                bank_head_code = @BankHeadCode, bank_head_name = @BankHeadName,
                                to_credit_amount = @ToCreditAmount,
                                last_modified_by = @LastModifiedBy, last_modified_date = @LastModifiedDate
                            WHERE entry_no = @EntryNo",
                            new
                            {
                                summary.EntryNo,
                                EntryDate = summary.EntryDate.ToString("yyyy-MM-dd"),
                                summary.OriginalBillNo,
                                summary.BillType,
                                summary.VendorCode,
                                summary.VendorName,
                                summary.VendorAddress,
                                summary.VendorPhone,
                                summary.VendorGstNo,
                                summary.State,
                                summary.StateCode,
                                summary.GrossAmount,
                                summary.DiscountAmount,
                                summary.DiscountPercent,
                                summary.TaxAmount,
                                summary.RoundOff,
                                summary.NetTotal,
                                summary.ToCashAmount,
                                summary.ToBankAmount,
                                summary.BankHeadCode,
                                summary.BankHeadName,
                                summary.ToCreditAmount,
                                summary.LastModifiedBy,
                                LastModifiedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                            }, tx);

                        InsertItemsAndStock(db, tx, summary.EntryNo, items);

                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
            }

            summary.FormattedEntryNo = FormatEntryNo(summary.EntryNo);
            PostToAccounts(summary, items, summary.LastModifiedBy);
        }

        private void InsertItemsAndStock(IDbConnection db, IDbTransaction tx, int entryNo, List<PurchaseReturnItemDetail> items)
        {
            int slNo = 1;
            foreach (var it in items)
            {
                // Purchase return DECREASES stock — qty_out on the batch
                // being sent back to the vendor.
                int transId = _stockRepo.InsertLedger(db, tx, DateTime.Now.ToString("yyyy-MM-dd"),
                    "PUR_RETURN", it.BatchId, qtyIn: 0, qtyOut: it.Qty);

                db.Execute($@"
                    INSERT INTO purchase_return_item_details ({ItemColumns})
                    VALUES (@EntryNo, @SlNo, @ItemCode, @ItemName, @BatchId, @BatchNumber, @TransId,
                            @Qty, @Rate, @DiscountPercent, @DiscountAmount, @TaxableAmount,
                            @TaxPercent, @TaxAmount, @TotalAmount)",
                    new
                    {
                        EntryNo = entryNo,
                        SlNo = slNo++,
                        it.ItemCode,
                        it.ItemName,
                        it.BatchId,
                        it.BatchNumber,
                        TransId = transId,
                        it.Qty,
                        it.Rate,
                        it.DiscountPercent,
                        it.DiscountAmount,
                        it.TaxableAmount,
                        it.TaxPercent,
                        it.TaxAmount,
                        it.TotalAmount
                    }, tx);
            }
        }

        public PurchaseReturnSummary GetSummary(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var s = db.QueryFirstOrDefault<PurchaseReturnSummary>(
                    "SELECT * FROM purchase_return_summary WHERE entry_no = @EntryNo",
                    new { EntryNo = entryNo });
                if (s != null) s.FormattedEntryNo = FormatEntryNo(s.EntryNo);
                return s;
            }
        }

        public List<PurchaseReturnItemDetail> GetItems(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<PurchaseReturnItemDetail>(
                    "SELECT * FROM purchase_return_item_details WHERE entry_no = @EntryNo ORDER BY sl_no",
                    new { EntryNo = entryNo }).ToList();
            }
        }

        public void CancelEntry(int entryNo, string cancelledBy)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        var transIds = db.Query<int>(
                            "SELECT trans_id FROM purchase_return_item_details WHERE entry_no = @EntryNo AND trans_id > 0",
                            new { EntryNo = entryNo }, tx);
                        foreach (var tid in transIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, tid);

                        db.Execute(@"UPDATE purchase_return_summary
                                      SET is_cancelled = 1, last_modified_by = @By, last_modified_date = @Date
                                      WHERE entry_no = @EntryNo",
                            new { EntryNo = entryNo, By = cancelledBy, Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }, tx);

                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
            }

            var cancelled = GetSummary(entryNo);
            try
            {
                _financialSync.Reverse(TransactionType.PurchaseReturn, cancelled.FormattedEntryNo,
                    cancelled.EntryDate.ToString("yyyy-MM-dd"));
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(acDb, "purchase_return_summary", "entry_no=@E", new { E = entryNo });
            }
            catch
            {
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(acDb, "purchase_return_summary", "entry_no=@E", new { E = entryNo });
            }
        }
    }
}