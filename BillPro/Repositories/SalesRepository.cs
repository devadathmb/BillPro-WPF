using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public partial class SalesRepository
    {
        private readonly StockMasterRepository _stockRepo = new StockMasterRepository();
        private readonly SalesSeriesRepository _seriesRepo = new SalesSeriesRepository();
        private readonly FinancialSyncService _financialSync = new FinancialSyncService();

        private static bool _initialized = false;
        private static readonly object _initLock = new();

        public void InitializeDatabase()
        {
            // Every view's constructor calls this, so without a guard,
            // opening two tabs close together races two ALTER TABLE
            // attempts against each other and one loses with "database is
            // locked". Schema setup only ever needs to happen once per run.
            lock (_initLock)
            {
                if (_initialized) return;

                _stockRepo.InitializeDatabase();
                _seriesRepo.InitializeDatabase();

                using (IDbConnection db = DatabaseConfig.GetConnection())
                {
                    db.Execute(@"
                    CREATE TABLE IF NOT EXISTS sales_summary (
                        prefix              TEXT NOT NULL,
                        bill_no             INTEGER NOT NULL,
                        formatted_bill_no   TEXT NOT NULL UNIQUE,
                        bill_date           TEXT NOT NULL,
                        bill_type           TEXT NOT NULL,       -- Cash / Credit
                        customer_code       TEXT,
                        customer_name       TEXT,
                        customer_address    TEXT,
                        customer_phone      TEXT,
                        customer_gst_no     TEXT,
                        state               TEXT,
                        state_code          TEXT,
                        gross_amount        REAL DEFAULT 0,
                        discount_amount     REAL DEFAULT 0,
                        tax_amount          REAL DEFAULT 0,
                        round_off           REAL DEFAULT 0,
                        net_total           REAL DEFAULT 0,
                        transporter_name    TEXT,
                        vehicle_no          TEXT,
                        lr_no               TEXT,
                        lr_date             TEXT,
                        eway_bill_no        TEXT,
                        is_cancelled        INTEGER NOT NULL DEFAULT 0,
                        created_by          TEXT,
                        created_date        TEXT,
                        last_modified_by    TEXT,
                        last_modified_date  TEXT,
                        PRIMARY KEY (prefix, bill_no)
                    );

                    CREATE TABLE IF NOT EXISTS sales_item_details (
                        prefix              TEXT NOT NULL,
                        bill_no             INTEGER NOT NULL,
                        sl_no               INTEGER NOT NULL,
                        item_code           TEXT NOT NULL,
                        item_name           TEXT,
                        batch_id            INTEGER NOT NULL,
                        trans_id            INTEGER NOT NULL,
                        qty                 REAL DEFAULT 0,
                        rate                REAL DEFAULT 0,
                        discount_percent    REAL DEFAULT 0,
                        discount_amount     REAL DEFAULT 0,
                        taxable_amount      REAL DEFAULT 0,
                        tax_percent         REAL DEFAULT 0,
                        tax_amount          REAL DEFAULT 0,
                        total_amount        REAL DEFAULT 0,
                        PRIMARY KEY (prefix, bill_no, sl_no)
                    );");

                    // Self-heal: settlement / bill-discount / extra delivery columns
                    // added after the initial schema — existing installs pick these
                    // up automatically without a manual migration step.
                    AddColumnIfMissing(db, "sales_summary", "discount_percent", "REAL DEFAULT 0");
                    AddColumnIfMissing(db, "sales_summary", "cash_discount", "REAL DEFAULT 0");
                    AddColumnIfMissing(db, "sales_summary", "to_cash_amount", "REAL DEFAULT 0");
                    AddColumnIfMissing(db, "sales_summary", "to_bank_amount", "REAL DEFAULT 0");
                    AddColumnIfMissing(db, "sales_summary", "bank_head_code", "INTEGER DEFAULT 0");
                    AddColumnIfMissing(db, "sales_summary", "bank_head_name", "TEXT");
                    AddColumnIfMissing(db, "sales_summary", "to_credit_amount", "REAL DEFAULT 0");
                    AddColumnIfMissing(db, "sales_summary", "place_of_delivery", "TEXT");
                    AddColumnIfMissing(db, "sales_summary", "date_of_delivery", "TEXT");

                    // Tracks whether this bill's tax-ledger posting to
                    // accounts.db succeeded — see FinancialSyncService /
                    // SalesRepository.Accounts.cs / AccountsResyncService.
                    AddColumnIfMissing(db, "sales_summary", "accounts_synced", "INTEGER NOT NULL DEFAULT 1");

                    // MRP wasn't originally stored per line — it was only ever
                    // shown transiently while picking the batch in the entry
                    // combo, never persisted or reloaded. Self-heal adds it so
                    // existing installs pick it up without a manual migration;
                    // pre-existing rows will read back as 0 until re-saved,
                    // same as any other newly-added column would.
                    AddColumnIfMissing(db, "sales_item_details", "mrp", "REAL DEFAULT 0");

                    // Ex-tax per-unit price, backed out of the tax-inclusive
                    // Rate at entry time (see SalesEntryView.ComputeUnitPrice).
                    // Persisted rather than only ever re-derived from
                    // rate/tax_percent on load, so it survives future changes
                    // to the formula, historical bills keep showing exactly
                    // what they were calculated at, and any future
                    // report/customization built on Unit Price doesn't need
                    // to re-derive it from Rate every time.
                    AddColumnIfMissing(db, "sales_item_details", "unit_price", "REAL DEFAULT 0");
                }

                _initialized = true;
            }
        }

        /// <summary>Adds a column to a table if it doesn't already exist (SQLite-safe migration helper).</summary>
        private static void AddColumnIfMissing(IDbConnection db, string table, string column, string columnDefinition)
        {
            var existingColumns = db.Query<string>($"SELECT name FROM pragma_table_info('{table}')")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!existingColumns.Contains(column))
                db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}");
        }

        /// <summary>Account heads under the reserved Banks category — used by
        /// the settlement panel's By Bank combo. Delegates to the shared
        /// BillPro.DataAccess.BankAccountLookup so every entry screen's bank
        /// combo (Sales, Purchase, Sales/Purchase Return, Receipt, Payment)
        /// draws from the exact same query.</summary>
        public List<AcHeadLookup> GetBankAcHeads() => new AccountHeadRepository().GetBankAcHeadLookups();

        // ────────────────────────────────────────────────────────────────
        // BATCH LOOKUP — one row per batch, not aggregated. Customers pick
        // the exact batch manually, so this has to mirror what's physically
        // on the shelf. Items with zero batches at all get a single
        // synthetic BatchId=0 "(New Batch)" row so they're still sellable
        // (Path B) when the checkbox allows 0/negative stock.
        // ────────────────────────────────────────────────────────────────
        public List<SalesItemLookup> GetSalesBatchLookups(bool includeZeroOrNegativeStock)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                // CAST ensures SQLite gives Dapper the exact data types C# is expecting,
                // preventing silent Int64-to-Int32 or Integer-to-Double crashes.
                string sql = @"
            SELECT 
                CAST(COALESCE(b.batch_id, 0) AS INTEGER) AS BatchId,
                m.item_code                              AS ItemCode,
                m.item_name                              AS ItemName,
                m.hsn_code                               AS HsnCode,
                b.batch_number                           AS BatchNumber,
                b.expiry_date                            AS ExpiryDate,
                CAST(COALESCE(b.mrp, m.mrp) AS REAL)     AS Mrp,
                CAST(COALESCE(b.purchase_rate, m.unit_price_purchase) AS REAL) AS PurchaseRate,
                CAST(COALESCE(b.sales_rate, m.unit_price_sales) AS REAL)       AS SalesRate,
                CAST(m.tax_rate AS REAL)                 AS TaxRate,
                CAST((COALESCE(SUM(t.qty_in), 0) - COALESCE(SUM(t.qty_out), 0)) AS REAL) AS CurrentStock
            FROM item_master m
            LEFT JOIN item_batch_details b ON m.item_code = b.item_code
            LEFT JOIN stock_transaction_details t ON t.batch_id = b.batch_id
            WHERE m.is_active IN (0)
            GROUP BY m.item_code, b.batch_id
            ORDER BY m.item_name, Mrp";

                var allRows = db.Query<SalesItemLookup>(sql).ToList();

                if (includeZeroOrNegativeStock)
                {
                    return allRows;
                }
                else
                {
                    return allRows.Where(r => r.CurrentStock > 0).ToList();
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // SAVE — claims the bill number and writes everything inside one
        // transaction: series increment, summary, each line's batch
        // resolution + SALE ledger post + item detail row.
        // ────────────────────────────────────────────────────────────────
        public (string Prefix, int BillNo, string FormattedBillNo) Save(
            string prefix, SalesSummary summary, List<SalesLine> lines, string currentUser)
        {
            int billNo;
            string formattedBillNo;

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        (billNo, formattedBillNo) = _seriesRepo.ClaimNextBillNo(db, tx, prefix);
                        string now = DateTime.Now.ToString("s");
                        string billDate = summary.BillDate.ToString("yyyy-MM-dd");

                        InsertSummary(db, tx, prefix, billNo, formattedBillNo, summary, currentUser, now, includeModifiedColumns: false);

                        int slNo = 1;
                        foreach (var line in lines)
                        {
                            InsertLine(db, tx, prefix, billNo, slNo, billDate, line);
                            slNo++;
                        }

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            // Inventory.db is safely committed at this point — accounts
            // posting is a separate database/transaction and must never
            // roll back a bill that already saved. Failures degrade to
            // accounts_synced = 0 instead of throwing (see ResyncPending).
            PostToAccounts(prefix, billNo, formattedBillNo, summary, lines, currentUser);
            return (prefix, billNo, formattedBillNo);
        }

        // ────────────────────────────────────────────────────────────────
        // UPDATE — reverses old ledger rows for this bill's lines (by their
        // stored trans_id, batches themselves untouched), deletes old lines,
        // re-inserts fresh ones. Bill number/prefix never change on update.
        // ────────────────────────────────────────────────────────────────
        public void Update(string prefix, int billNo, SalesSummary summary, List<SalesLine> lines, string currentUser)
        {
            string formattedBillNo;

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        // The formatted bill number is claimed once at Save
                        // time (ClaimNextBillNo) and never changes on Update
                        // (see this method's header comment) — read it back
                        // from the row itself rather than trust
                        // summary.FormattedBillNo. The entry screen builds a
                        // brand new SalesSummary purely from UI fields on
                        // every Save/Update (see SalesEntryView
                        // .BuildSummaryFromUI), and previously that field
                        // came through null. PostToAccounts uses it as the
                        // voucher's VoucherRef, which FinancialSyncService
                        // matches against voucher_tax_postings to find and
                        // reverse this bill's PRIOR postings before writing
                        // the new ones — a null/mismatched ref meant that
                        // lookup silently matched nothing, so the prior
                        // posting (on the bill's old date, old amounts,
                        // whatever it was) was never reversed and stayed
                        // sitting in the Daybook/Ledger while a new, orphaned
                        // posting landed elsewhere. Any edit — not just a
                        // date change — hit this, since it depends on
                        // nothing else about what fields changed.
                        formattedBillNo = db.ExecuteScalar<string>(
                            "SELECT formatted_bill_no FROM sales_summary WHERE prefix = @Prefix AND bill_no = @BillNo",
                            new { Prefix = prefix, BillNo = billNo }, tx);

                        var oldTransIds = db.Query<int>(
                            "SELECT trans_id FROM sales_item_details WHERE prefix = @Prefix AND bill_no = @BillNo",
                            new { Prefix = prefix, BillNo = billNo }, tx).ToList();

                        foreach (var transId in oldTransIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, transId);

                        db.Execute("DELETE FROM sales_item_details WHERE prefix = @Prefix AND bill_no = @BillNo",
                            new { Prefix = prefix, BillNo = billNo }, tx);

                        string now = DateTime.Now.ToString("s");
                        string billDate = summary.BillDate.ToString("yyyy-MM-dd");

                        UpdateSummary(db, tx, prefix, billNo, summary, currentUser, now);

                        int slNo = 1;
                        foreach (var line in lines)
                        {
                            InsertLine(db, tx, prefix, billNo, slNo, billDate, line);
                            slNo++;
                        }

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            // Keep the caller's summary object consistent too, in case it's
            // reused after Update returns (e.g. built into an InvoiceData).
            summary.FormattedBillNo = formattedBillNo;

            // NOTE: no explicit Reverse() needed here — FinancialSyncService
            // .Post/PostOnTransaction already reverses this voucher's prior
            // live postings by (TransactionType, VoucherRef) before writing
            // the new legs, so calling PostToAccounts again on Update is
            // self-correcting on its own — PROVIDED VoucherRef actually
            // matches the prior posting, which is why formattedBillNo is
            // read back from the row above rather than taken from
            // summary.FormattedBillNo. See FinancialSyncService's class doc
            // comment.
            PostToAccounts(prefix, billNo, formattedBillNo, summary, lines, currentUser);
        }

        // ────────────────────────────────────────────────────────────────
        // CANCEL — soft delete. The bill row stays (is_cancelled = 1) but
        // every ledger entry it created is reversed, so stock comes back and
        // the bill drops out of every stock/sales calculation. Never
        // physically deletes sales_summary/sales_item_details.
        // ────────────────────────────────────────────────────────────────
        public void CancelBill(string prefix, int billNo, string currentUser)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        var transIds = db.Query<int>(
                            "SELECT trans_id FROM sales_item_details WHERE prefix = @Prefix AND bill_no = @BillNo",
                            new { Prefix = prefix, BillNo = billNo }, tx).ToList();

                        foreach (var transId in transIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, transId);

                        db.Execute(@"
                            UPDATE sales_summary
                            SET is_cancelled = 1, last_modified_by = @User, last_modified_date = @Now
                            WHERE prefix = @Prefix AND bill_no = @BillNo",
                            new { User = currentUser, Now = DateTime.Now.ToString("s"), Prefix = prefix, BillNo = billNo }, tx);

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            var cancelledBill = GetSummary(prefix, billNo);
            try
            {
                _financialSync.Reverse(TransactionType.Sales, cancelledBill.FormattedBillNo,
                    cancelledBill.BillDate.ToString("yyyy-MM-dd"));
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(acDb, "sales_summary", "prefix=@P AND bill_no=@B",
                        new { P = prefix, B = billNo });
            }
            catch
            {
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(acDb, "sales_summary", "prefix=@P AND bill_no=@B",
                        new { P = prefix, B = billNo });
            }
        }

        // ────────────────────────────────────────────────────────────────
        // LOAD FOR DISPLAY/EDIT
        // ────────────────────────────────────────────────────────────────
        public SalesSummary GetSummary(string prefix, int billNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<SalesSummary>(SummarySelectColumns +
                    " FROM sales_summary WHERE prefix = @Prefix AND bill_no = @BillNo",
                    new { Prefix = prefix, BillNo = billNo });
            }
        }

        public SalesSummary GetSummaryByFormattedBillNo(string formattedBillNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<SalesSummary>(SummarySelectColumns +
                    " FROM sales_summary WHERE formatted_bill_no = @FormattedBillNo",
                    new { FormattedBillNo = formattedBillNo });
            }
        }

        public List<SalesItemDetail> GetItems(string prefix, int billNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<SalesItemDetail>(@"
                    SELECT d.prefix AS Prefix, d.bill_no AS BillNo, d.sl_no AS SlNo,
                           d.item_code AS ItemCode, d.item_name AS ItemName,
                           d.batch_id AS BatchId, b.batch_number AS BatchNumber, d.trans_id AS TransId,
                           d.mrp AS Mrp,
                           d.qty AS Qty, d.rate AS Rate, d.unit_price AS UnitPrice,
                           d.discount_percent AS DiscountPercent, d.discount_amount AS DiscountAmount,
                           d.taxable_amount AS TaxableAmount, d.tax_percent AS TaxPercent, d.tax_amount AS TaxAmount,
                           d.total_amount AS TotalAmount
                    FROM sales_item_details d
                    LEFT JOIN item_batch_details b ON b.batch_id = d.batch_id
                    WHERE d.prefix = @Prefix AND d.bill_no = @BillNo
                    ORDER BY d.sl_no",
                    new { Prefix = prefix, BillNo = billNo }).ToList();
            }
        }

        public List<SalesSummary> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<SalesSummary>(SummarySelectColumns +
                    " FROM sales_summary ORDER BY bill_date DESC, bill_no DESC").ToList();
            }
        }

        /// <summary>
        /// Every non-cancelled bill in one series on one date — used to
        /// resolve a grouped Daybook/Ledger Report row (Narration
        /// "billFrom - billTo") back to the individual bills it summarizes,
        /// for the "pick a bill to edit" popup. A grouped bucket is keyed by
        /// (TransactionType, EntryDate, Series, TaxPercent, LedgerRole,
        /// LedgerId) — see FinancialSyncService.UpsertGroupedDaybookRow —
        /// so filtering by series + date alone is deliberately a bit wider
        /// than one exact bucket (a bill can touch more than one tax slab);
        /// that's fine here since the point is just "which bills posted
        /// that day under this series", same "approximate, reference-only"
        /// spirit as bill_no_from/bill_no_to themselves.
        /// </summary>
        public List<SalesSummary> GetBillsBySeriesAndDate(string series, DateTime date)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<SalesSummary>(SummarySelectColumns +
                    @" FROM sales_summary
                       WHERE prefix = @Series AND bill_date = @Date AND is_cancelled = 0
                       ORDER BY bill_no",
                    new { Series = series, Date = date.ToString("yyyy-MM-dd") }).ToList();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // SHARED HELPERS
        // ────────────────────────────────────────────────────────────────
        private const string SummarySelectColumns = @"
            SELECT prefix              AS Prefix,
                   bill_no             AS BillNo,
                   formatted_bill_no   AS FormattedBillNo,
                   bill_date           AS BillDate,
                   bill_type           AS BillType,
                   customer_code       AS CustomerCode,
                   customer_name       AS CustomerName,
                   customer_address    AS CustomerAddress,
                   customer_phone      AS CustomerPhone,
                   customer_gst_no     AS CustomerGstNo,
                   state               AS State,
                   state_code          AS StateCode,
                   gross_amount        AS GrossAmount,
                   discount_amount     AS DiscountAmount,
                   discount_percent    AS DiscountPercent,
                   tax_amount          AS TaxAmount,
                   cash_discount       AS CashDiscount,
                   round_off           AS RoundOff,
                   net_total           AS NetTotal,
                   to_cash_amount      AS ToCashAmount,
                   to_bank_amount      AS ToBankAmount,
                   bank_head_code      AS BankHeadCode,
                   bank_head_name      AS BankHeadName,
                   to_credit_amount    AS ToCreditAmount,
                   transporter_name    AS TransporterName,
                   vehicle_no          AS VehicleNo,
                   lr_no               AS LrNo,
                   lr_date             AS LrDate,
                   eway_bill_no        AS EwayBillNo,
                   place_of_delivery   AS PlaceOfDelivery,
                   date_of_delivery    AS DateOfDelivery,
                   is_cancelled        AS IsCancelled,
                   created_by          AS CreatedBy,
                   created_date        AS CreatedDate,
                   last_modified_by    AS LastModifiedBy,
                   last_modified_date  AS LastModifiedDate";

        private void InsertSummary(IDbConnection db, IDbTransaction tx, string prefix, int billNo,
            string formattedBillNo, SalesSummary s, string user, string now, bool includeModifiedColumns)
        {
            db.Execute(@"
                INSERT INTO sales_summary (
                    prefix, bill_no, formatted_bill_no, bill_date, bill_type,
                    customer_code, customer_name, customer_address, customer_phone,
                    customer_gst_no, state, state_code,
                    gross_amount, discount_amount, discount_percent, tax_amount,
                    cash_discount, round_off, net_total,
                    to_cash_amount, to_bank_amount, bank_head_code, bank_head_name, to_credit_amount,
                    transporter_name, vehicle_no, lr_no, lr_date, eway_bill_no,
                    place_of_delivery, date_of_delivery,
                    is_cancelled, created_by, created_date
                ) VALUES (
                    @Prefix, @BillNo, @FormattedBillNo, @BillDate, @BillType,
                    @CustomerCode, @CustomerName, @CustomerAddress, @CustomerPhone,
                    @CustomerGstNo, @State, @StateCode,
                    @GrossAmount, @DiscountAmount, @DiscountPercent, @TaxAmount,
                    @CashDiscount, @RoundOff, @NetTotal,
                    @ToCashAmount, @ToBankAmount, @BankHeadCode, @BankHeadName, @ToCreditAmount,
                    @TransporterName, @VehicleNo, @LrNo, @LrDate, @EwayBillNo,
                    @PlaceOfDelivery, @DateOfDelivery,
                    0, @CreatedBy, @CreatedDate
                )",
                new
                {
                    Prefix = prefix,
                    BillNo = billNo,
                    FormattedBillNo = formattedBillNo,
                    BillDate = s.BillDate.ToString("yyyy-MM-dd"),
                    s.BillType,
                    s.CustomerCode,
                    s.CustomerName,
                    s.CustomerAddress,
                    s.CustomerPhone,
                    s.CustomerGstNo,
                    s.State,
                    s.StateCode,
                    s.GrossAmount,
                    s.DiscountAmount,
                    s.DiscountPercent,
                    s.TaxAmount,
                    s.CashDiscount,
                    s.RoundOff,
                    s.NetTotal,
                    s.ToCashAmount,
                    s.ToBankAmount,
                    s.BankHeadCode,
                    s.BankHeadName,
                    s.ToCreditAmount,
                    s.TransporterName,
                    s.VehicleNo,
                    s.LrNo,
                    s.LrDate,
                    s.EwayBillNo,
                    s.PlaceOfDelivery,
                    s.DateOfDelivery,
                    CreatedBy = user,
                    CreatedDate = now
                }, tx);
        }

        private void UpdateSummary(IDbConnection db, IDbTransaction tx, string prefix, int billNo,
            SalesSummary s, string user, string now)
        {
            db.Execute(@"
                UPDATE sales_summary SET
                    bill_date = @BillDate, bill_type = @BillType,
                    customer_code = @CustomerCode, customer_name = @CustomerName,
                    customer_address = @CustomerAddress, customer_phone = @CustomerPhone,
                    customer_gst_no = @CustomerGstNo, state = @State, state_code = @StateCode,
                    gross_amount = @GrossAmount, discount_amount = @DiscountAmount,
                    discount_percent = @DiscountPercent, tax_amount = @TaxAmount,
                    cash_discount = @CashDiscount, round_off = @RoundOff, net_total = @NetTotal,
                    to_cash_amount = @ToCashAmount, to_bank_amount = @ToBankAmount,
                    bank_head_code = @BankHeadCode, bank_head_name = @BankHeadName,
                    to_credit_amount = @ToCreditAmount,
                    transporter_name = @TransporterName, vehicle_no = @VehicleNo,
                    lr_no = @LrNo, lr_date = @LrDate, eway_bill_no = @EwayBillNo,
                    place_of_delivery = @PlaceOfDelivery, date_of_delivery = @DateOfDelivery,
                    last_modified_by = @User, last_modified_date = @Now
                WHERE prefix = @Prefix AND bill_no = @BillNo",
                new
                {
                    BillDate = s.BillDate.ToString("yyyy-MM-dd"),
                    s.BillType,
                    s.CustomerCode,
                    s.CustomerName,
                    s.CustomerAddress,
                    s.CustomerPhone,
                    s.CustomerGstNo,
                    s.State,
                    s.StateCode,
                    s.GrossAmount,
                    s.DiscountAmount,
                    s.DiscountPercent,
                    s.TaxAmount,
                    s.CashDiscount,
                    s.RoundOff,
                    s.NetTotal,
                    s.ToCashAmount,
                    s.ToBankAmount,
                    s.BankHeadCode,
                    s.BankHeadName,
                    s.ToCreditAmount,
                    s.TransporterName,
                    s.VehicleNo,
                    s.LrNo,
                    s.LrDate,
                    s.EwayBillNo,
                    s.PlaceOfDelivery,
                    s.DateOfDelivery,
                    User = user,
                    Now = now,
                    Prefix = prefix,
                    BillNo = billNo
                }, tx);
        }

        private void InsertLine(IDbConnection db, IDbTransaction tx, string prefix, int billNo, int slNo,
            string billDate, SalesLine line)
        {
            // line.BatchId is the batch the customer actually picked off the
            // shelf (per SalesItemLookup.BatchId at add-time) — used as-is.
            // BatchId == 0 is the synthetic "(New Batch)" placeholder for an
            // item that's never been purchased before: only then do we
            // create a real batch now, from item_master's defaults (Path B),
            // same pattern as Opening Stock / Purchase Entry.
            int batchId = line.BatchId;

            if (batchId <= 0)
            {
                var item = db.QueryFirstOrDefault<ItemMaster>(
                    "SELECT mrp AS Mrp, unit_price_purchase AS UnitPricePurchase, unit_price_sales AS UnitPriceSales FROM item_master WHERE item_code = @ItemCode",
                    new { ItemCode = line.ItemCode }, tx);

                double mrp = item?.Mrp ?? (double)line.Mrp;
                double purchaseRate = item?.UnitPricePurchase ?? 0;
                double salesRate = item?.UnitPriceSales ?? (double)line.Rate;

                batchId = _stockRepo.GetOrCreateBatch(db, tx, line.ItemCode, mrp, purchaseRate, salesRate);
            }

            int transId = _stockRepo.InsertLedger(
                db, tx, transDate: billDate, transType: "SALE",
                batchId: batchId, qtyIn: 0, qtyOut: (double)line.Quantity);

            db.Execute(@"
                INSERT INTO sales_item_details (
                    prefix, bill_no, sl_no, item_code, item_name, batch_id, trans_id,
                    mrp, qty, rate, unit_price, discount_percent, discount_amount,
                    taxable_amount, tax_percent, tax_amount, total_amount
                ) VALUES (
                    @Prefix, @BillNo, @SlNo, @ItemCode, @ItemName, @BatchId, @TransId,
                    @Mrp, @Qty, @Rate, @UnitPrice, @DiscountPercent, @DiscountAmount,
                    @TaxableAmount, @TaxPercent, @TaxAmount, @TotalAmount
                )",
                new
                {
                    Prefix = prefix,
                    BillNo = billNo,
                    SlNo = slNo,
                    line.ItemCode,
                    line.ItemName,
                    BatchId = batchId,
                    TransId = transId,
                    Mrp = line.Mrp,
                    Qty = line.Quantity,
                    line.Rate,
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountRate,
                    DiscountAmount = line.Discount,
                    TaxableAmount = line.TaxableAmount,
                    TaxPercent = line.TaxRate,
                    TaxAmount = line.TaxAmount,
                    TotalAmount = line.Total
                }, tx);

            // Reflect the resolved batch back onto the line — matters when
            // BatchId was 0 (synthetic) and just became a real batch_id, so
            // a re-displayed bill shows the actual batch it sold against.
            line.BatchId = batchId;
            line.TransId = transId;
        }
    }
}