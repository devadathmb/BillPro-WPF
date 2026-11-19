// SalesReturnRepository.cs
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
    // SALES RETURN — increases stock (qty_in, trans_type "SALE_RETURN").
    // No series master: entry_no is a single global autoincrement, formatted
    // as "SR-{n}" purely for display/print — never re-parsed, never reused
    // as a lookup key (entry_no itself is the real PK).
    // ═══════════════════════════════════════════════════════════════════════
    public partial class SalesReturnRepository
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new();
        private readonly StockMasterRepository _stockRepo = new();
        private readonly FinancialSyncService _financialSync = new();

        private const string ItemColumns = @"
            entry_no, sl_no, item_code, item_name, batch_id, batch_number, trans_id,
            qty, rate, unit_price, discount_percent, discount_amount, taxable_amount,
            tax_percent, tax_amount, total_amount";

        public void InitializeDatabase()
        {
            lock (_initLock)
            {
                if (_initialized) return;
                _stockRepo.InitializeDatabase();

                using (IDbConnection db = DatabaseConfig.GetConnection())
                {
                    db.Execute(@"
                        CREATE TABLE IF NOT EXISTS sales_return_summary (
                            entry_no            INTEGER PRIMARY KEY AUTOINCREMENT,
                            entry_date          TEXT NOT NULL,
                            original_bill_no    TEXT,
                            bill_type           TEXT NOT NULL,
                            customer_code       TEXT,
                            customer_name       TEXT,
                            customer_address    TEXT,
                            customer_phone      TEXT,
                            customer_gst_no     TEXT,
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

                        CREATE TABLE IF NOT EXISTS sales_return_item_details (
                            entry_no          INTEGER NOT NULL,
                            sl_no             INTEGER NOT NULL,
                            item_code         TEXT NOT NULL,
                            item_name         TEXT,
                            batch_id          INTEGER NOT NULL,
                            batch_number      TEXT,
                            trans_id          INTEGER,
                            qty               REAL DEFAULT 0,
                            rate              REAL DEFAULT 0,
                            unit_price        REAL DEFAULT 0,
                            discount_percent  REAL DEFAULT 0,
                            discount_amount   REAL DEFAULT 0,
                            taxable_amount    REAL DEFAULT 0,
                            tax_percent       REAL DEFAULT 0,
                            tax_amount        REAL DEFAULT 0,
                            total_amount      REAL DEFAULT 0,
                            PRIMARY KEY (entry_no, sl_no)
                        );");

                    AccountsSyncStatus.EnsureColumn(db, "sales_return_summary");

                    // Self-heal for databases that already had this table
                    // before unit_price existed — CREATE TABLE IF NOT EXISTS
                    // above is a no-op once the table's already there.
                    try { db.Execute("ALTER TABLE sales_return_item_details ADD COLUMN unit_price REAL DEFAULT 0;"); }
                    catch { /* column already exists */ }
                }

                _initialized = true;
            }
        }

        public string FormatEntryNo(int entryNo) => $"SR-{entryNo}";

        /// <summary>Provisional next entry number for display only — same
        /// "preview, not claim" convention as Sales Entry's Bill No preview.
        /// The real number is whatever SQLite's autoincrement actually
        /// assigns inside Save(); this is just MAX(entry_no)+1 so the
        /// operator isn't staring at a bare "(auto)" placeholder.</summary>
        public int GetNextEntryNoPreview()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<int>(
                    "SELECT COALESCE(MAX(entry_no), 0) + 1 FROM sales_return_summary");
            }
        }

        /// <summary>Best-effort check that the typed bill no matches a real,
        /// non-cancelled sales bill. Non-blocking by design — the operator
        /// can still proceed against an unrecognized/legacy bill no.</summary>
        public bool OriginalBillExists(string formattedBillNo)
        {
            if (string.IsNullOrWhiteSpace(formattedBillNo)) return false;
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM sales_summary WHERE formatted_bill_no = @No AND is_cancelled = 0",
                    new { No = formattedBillNo }) > 0;
            }
        }

        /// <summary>Account heads under the reserved Banks category — used by
        /// the settlement panel's By Bank combo. Delegates to the shared
        /// BillPro.DataAccess.BankAccountLookup so every entry screen's bank
        /// combo draws from the exact same query. Gives SalesReturnEntryView
        /// its own repository to depend on instead of reaching into
        /// SalesRepository for this.</summary>
        public List<AcHeadLookup> GetBankAcHeads() => new AccountHeadRepository().GetBankAcHeadLookups();

        public List<SalesReturnItemLookup> GetReturnBatchLookups()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<SalesReturnItemLookup>(@"
                    SELECT
                        v.batch_id       AS BatchId,
                        v.item_code      AS ItemCode,
                        m.item_name      AS ItemName,
                        m.hsn_code       AS HsnCode,
                        v.batch_number   AS BatchNumber,
                        CAST(v.mrp         AS REAL) AS Mrp,
                        CAST(v.sales_rate  AS REAL) AS SalesRate,
                        CAST(m.tax_rate    AS REAL) AS TaxRate,
                        CAST(v.current_qty AS REAL) AS CurrentStock
                    FROM View_CurrentStock v
                    JOIN item_master m ON m.item_code = v.item_code
                    ORDER BY m.item_name, v.mrp").ToList();
            }
        }

        public (int EntryNo, string Formatted) Save(SalesReturnSummary summary, List<SalesReturnItemDetail> items)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        db.Execute(@"
                            INSERT INTO sales_return_summary
                                (entry_date, original_bill_no, bill_type, customer_code, customer_name,
                                 customer_address, customer_phone, customer_gst_no, state, state_code,
                                 gross_amount, discount_amount, discount_percent, tax_amount, round_off, net_total,
                                 to_cash_amount, to_bank_amount, bank_head_code, bank_head_name, to_credit_amount,
                                 is_cancelled, created_by, created_date)
                            VALUES
                                (@EntryDate, @OriginalBillNo, @BillType, @CustomerCode, @CustomerName,
                                 @CustomerAddress, @CustomerPhone, @CustomerGstNo, @State, @StateCode,
                                 @GrossAmount, @DiscountAmount, @DiscountPercent, @TaxAmount, @RoundOff, @NetTotal,
                                 @ToCashAmount, @ToBankAmount, @BankHeadCode, @BankHeadName, @ToCreditAmount,
                                 0, @CreatedBy, @CreatedDate)",
                            new
                            {
                                EntryDate = summary.EntryDate.ToString("yyyy-MM-dd"),
                                summary.OriginalBillNo,
                                summary.BillType,
                                summary.CustomerCode,
                                summary.CustomerName,
                                summary.CustomerAddress,
                                summary.CustomerPhone,
                                summary.CustomerGstNo,
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

        public void Update(SalesReturnSummary summary, List<SalesReturnItemDetail> items)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        // Reverse old ledger effect first (same pattern as
                        // PurchaseEntryView's Update: delete-old-rows-by-parent
                        // then reinsert, but here we must also undo the qty_in
                        // that was added to stock, or re-saving would double it).
                        var oldTransIds = db.Query<int>(
                            "SELECT trans_id FROM sales_return_item_details WHERE entry_no = @EntryNo AND trans_id > 0",
                            new { EntryNo = summary.EntryNo }, tx);
                        foreach (var tid in oldTransIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, tid);

                        db.Execute("DELETE FROM sales_return_item_details WHERE entry_no = @EntryNo",
                            new { EntryNo = summary.EntryNo }, tx);

                        db.Execute(@"
                            UPDATE sales_return_summary SET
                                entry_date = @EntryDate, original_bill_no = @OriginalBillNo, bill_type = @BillType,
                                customer_code = @CustomerCode, customer_name = @CustomerName,
                                customer_address = @CustomerAddress, customer_phone = @CustomerPhone,
                                customer_gst_no = @CustomerGstNo, state = @State, state_code = @StateCode,
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
                                summary.CustomerCode,
                                summary.CustomerName,
                                summary.CustomerAddress,
                                summary.CustomerPhone,
                                summary.CustomerGstNo,
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

        private void InsertItemsAndStock(IDbConnection db, IDbTransaction tx, int entryNo, List<SalesReturnItemDetail> items)
        {
            int slNo = 1;
            foreach (var it in items)
            {
                // Sales return ADDS stock back — qty_in on the same batch it
                // was originally sold from.
                int transId = _stockRepo.InsertLedger(db, tx, DateTime.Now.ToString("yyyy-MM-dd"),
                    "SALE_RETURN", it.BatchId, qtyIn: it.Qty, qtyOut: 0);

                db.Execute($@"
                    INSERT INTO sales_return_item_details ({ItemColumns})
                    VALUES (@EntryNo, @SlNo, @ItemCode, @ItemName, @BatchId, @BatchNumber, @TransId,
                            @Qty, @Rate, @UnitPrice, @DiscountPercent, @DiscountAmount, @TaxableAmount,
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
                        it.UnitPrice,
                        it.DiscountPercent,
                        it.DiscountAmount,
                        it.TaxableAmount,
                        it.TaxPercent,
                        it.TaxAmount,
                        it.TotalAmount
                    }, tx);
            }
        }

        public SalesReturnSummary GetSummary(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var s = db.QueryFirstOrDefault<SalesReturnSummary>(
                    "SELECT * FROM sales_return_summary WHERE entry_no = @EntryNo",
                    new { EntryNo = entryNo });
                if (s != null) s.FormattedEntryNo = FormatEntryNo(s.EntryNo);
                return s;
            }
        }

        public List<SalesReturnItemDetail> GetItems(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<SalesReturnItemDetail>(
                    "SELECT * FROM sales_return_item_details WHERE entry_no = @EntryNo ORDER BY sl_no",
                    new { EntryNo = entryNo }).ToList();
            }
        }

        public List<SalesReturnSummary> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var list = db.Query<SalesReturnSummary>(
                    "SELECT * FROM sales_return_summary ORDER BY entry_date DESC, entry_no DESC"
                ).ToList();

                // Populate the display-friendly entry number for the grid
                foreach (var item in list)
                {
                    item.FormattedEntryNo = FormatEntryNo(item.EntryNo);
                }

                return list;
            }
        }

        /// <summary>
        /// Every non-cancelled return entry on one date — same role as
        /// SalesRepository.GetBillsBySeriesAndDate, for resolving a grouped
        /// Daybook/Ledger Report row back to individual entries. Sales
        /// Return has no series master (see class doc comment above), so
        /// unlike Sales there's nothing to additionally filter by — the
        /// "series" a grouped SalesReturn bucket resolves to is always the
        /// fixed "SR" fallback text FinancialSyncService.ResolveSeries
        /// derives from the "SR-{n}" voucher ref, not a real, filterable
        /// column here.
        /// </summary>
        public List<SalesReturnSummary> GetEntriesByDate(DateTime date)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var list = db.Query<SalesReturnSummary>(
                    @"SELECT * FROM sales_return_summary
                      WHERE entry_date = @Date AND is_cancelled = 0
                      ORDER BY entry_no",
                    new { Date = date.ToString("yyyy-MM-dd") }).ToList();

                foreach (var item in list)
                    item.FormattedEntryNo = FormatEntryNo(item.EntryNo);

                return list;
            }
        }

        /// <summary>Soft-cancel: reverses the stock effect (removes the qty_in
        /// that was added) and flags the entry, mirroring Sales Entry's
        /// CancelBill — never a hard delete.</summary>
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
                            "SELECT trans_id FROM sales_return_item_details WHERE entry_no = @EntryNo AND trans_id > 0",
                            new { EntryNo = entryNo }, tx);
                        foreach (var tid in transIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, tid);

                        db.Execute(@"UPDATE sales_return_summary
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
                _financialSync.Reverse(TransactionType.SalesReturn, cancelled.FormattedEntryNo,
                    cancelled.EntryDate.ToString("yyyy-MM-dd"));
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(acDb, "sales_return_summary", "entry_no=@E", new { E = entryNo });
            }
            catch
            {
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(acDb, "sales_return_summary", "entry_no=@E", new { E = entryNo });
            }
        }
    }
}