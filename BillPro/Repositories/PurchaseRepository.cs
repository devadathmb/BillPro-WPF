using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public partial class PurchaseRepository
    {
        private readonly StockMasterRepository _stockRepo = new StockMasterRepository();
        private readonly FinancialSyncService _financialSync = new FinancialSyncService();

        // ──────────────────────────────────────────────────────────────────────
        // SHARED SQL FRAGMENTS
        // Kept in one place so Save/Update/GetAll/GetSummaryByNo can't drift
        // apart from each other (the original bug — item_name/hsn_code missing
        // from the INSERT — happened because the column list was duplicated by
        // hand in multiple places).
        // ──────────────────────────────────────────────────────────────────────
        private const string SummarySelectColumns = @"
            id                 AS Id,
            purchase_no        AS PurchaseNo,
            purchase_date      AS PurchaseDate,
            bill_no            AS BillNo,
            bill_date          AS BillDate,
            purchase_mode      AS PurchaseMode,
            vendor_code        AS VendorCode,
            vendor_name        AS VendorName,
            address1           AS Address1,
            address2           AS Address2,
            address3           AS Address3,
            gst_no             AS GstNo,
            phone              AS Phone,
            mobile             AS Mobile,
            state              AS State,
            state_code         AS StateCode,
            price_amount       AS PriceAmount,
            tax_total          AS TaxTotal,
            grand_total        AS GrandTotal,
            cash_discount      AS CashDiscount,
            rounded_amount     AS RoundedAmount,
            net_total          AS NetTotal,
            to_card_amount     AS ToCardAmount,
            bank_head_code     AS BankHeadCode,
            bank_head_name     AS BankHeadName,
            to_cash_amount     AS ToCashAmount,
            to_credit_amount   AS ToCreditAmount,
            created_by         AS CreatedBy,
            created_date       AS CreatedDate,
            last_modified_by   AS LastModifiedBy,
            last_modified_date AS LastModifiedDate";

        private const string ItemLookupSelectColumns = @"
            item_code            AS ItemCode,
            item_name            AS ItemName,
            hsn_code             AS HsnCode,
            mrp                  AS Mrp,
            unit_price_purchase  AS UnitPricePurchase,
            unit_price_sales     AS UnitPriceSales,
            tax_rate             AS TaxRate,
            average_cost         AS AverageCost";

        private const string AcHeadSelectColumns = @"
            ac_head_code AS AcHeadCode,
            ac_head_name AS AcHeadName";

        // ──────────────────────────────────────────────────────────────────────
        // SCHEMA INITIALIZATION
        // ──────────────────────────────────────────────────────────────────────
        public void InitializeDatabase()
        {
            _stockRepo.InitializeDatabase();

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();

                db.Execute(@"
                    CREATE TABLE IF NOT EXISTS purchase_summary_details (
                        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                        purchase_no         INTEGER NOT NULL UNIQUE,
                        purchase_date       TEXT NOT NULL,
                        bill_no             TEXT,
                        bill_date           TEXT,
                        purchase_mode       INTEGER DEFAULT 0,
                        vendor_code         INTEGER DEFAULT 0,
                        vendor_name         TEXT,
                        address1            TEXT,
                        address2            TEXT,
                        address3            TEXT,
                        gst_no              TEXT,
                        phone               TEXT,
                        mobile              TEXT,
                        state               TEXT,
                        state_code          TEXT,
                        price_amount        REAL DEFAULT 0,
                        tax_total           REAL DEFAULT 0,
                        grand_total         REAL DEFAULT 0,
                        cash_discount       REAL DEFAULT 0,
                        rounded_amount      REAL DEFAULT 0,
                        net_total           REAL DEFAULT 0,
                        created_by          TEXT,
                        created_date        TEXT,
                        last_modified_by    TEXT,
                        last_modified_date  TEXT
                    );

                    CREATE TABLE IF NOT EXISTS purchase_item_details (
                        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                        purchase_no         INTEGER NOT NULL,
                        purchase_date       TEXT NOT NULL,
                        item_code           TEXT NOT NULL,
                        item_name           TEXT,
                        hsn_code            TEXT,
                        mrp                 REAL DEFAULT 0,
                        rate                REAL DEFAULT 0,
                        discount_rate       REAL DEFAULT 0, 
                        discount            REAL DEFAULT 0,
                        quantity            REAL DEFAULT 0,
                        tax_rate            REAL DEFAULT 0,
                        tax_amount          REAL DEFAULT 0,
                        price               REAL DEFAULT 0,
                        landing_cost        REAL DEFAULT 0,
                        landing_cost_stock  REAL DEFAULT 0
                    );

                    CREATE TABLE IF NOT EXISTS purchase_external_expenses (
                        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                        purchase_no         INTEGER NOT NULL,
                        purchase_date       TEXT NOT NULL,
                        ac_head_code        INTEGER NOT NULL,
                        narration           TEXT,
                        credit              REAL DEFAULT 0,
                        debit               REAL DEFAULT 0,
                        external_tax_rate   REAL DEFAULT 0,
                        external_tax        REAL DEFAULT 0
                    );");

                // Self-heal: if this DB was created before item_name/hsn_code
                // existed, add them now so existing installs pick up the fix
                // without needing a manual migration step.
                AddColumnIfMissing(db, "purchase_item_details", "item_name", "TEXT");
                AddColumnIfMissing(db, "purchase_item_details", "hsn_code", "TEXT");

                // Links each purchase line to the exact batch + ledger row it
                // created — the same "brilliant idea" pattern used by
                // opening_stock_details.trans_id/batch_id. Needed so Update/Delete
                // can cleanly reverse and relink instead of leaving orphaned or
                // duplicated ledger entries behind.
                AddColumnIfMissing(db, "purchase_item_details", "batch_id", "INTEGER");
                AddColumnIfMissing(db, "purchase_item_details", "trans_id", "INTEGER");

                // Tracks whether this bill's tax-ledger posting to
                // accounts.db succeeded — see FinancialSyncService /
                // PurchaseRepository.Accounts.cs / AccountsResyncService.
                AddColumnIfMissing(db, "purchase_summary_details", "accounts_synced", "INTEGER NOT NULL DEFAULT 1");

                // Settlement / money-transfer columns on the summary row
                // (how the bill was actually paid: card/bank, cash, or left
                // owing to the vendor as a creditor balance).
                AddColumnIfMissing(db, "purchase_summary_details", "to_card_amount", "REAL DEFAULT 0");
                AddColumnIfMissing(db, "purchase_summary_details", "bank_head_code", "INTEGER DEFAULT 0");
                AddColumnIfMissing(db, "purchase_summary_details", "bank_head_name", "TEXT");
                AddColumnIfMissing(db, "purchase_summary_details", "to_cash_amount", "REAL DEFAULT 0");
                AddColumnIfMissing(db, "purchase_summary_details", "to_credit_amount", "REAL DEFAULT 0");

                // item_master.average_cost backs the "Landing Cost" feature —
                // it lives in a table owned by the item-master module, but we
                // self-heal it here too in case that module hasn't been
                // updated yet. Best-effort: if item_master doesn't exist at
                // all yet, this silently no-ops.
                try { AddColumnIfMissing(db, "item_master", "average_cost", "REAL DEFAULT 0"); }
                catch { /* item_master not present yet — ItemMasterRepository will create it */ }
            }
        }

        /// <summary>Adds a column to a table if it doesn't already exist (SQLite-safe migration helper).</summary>
        private static void AddColumnIfMissing(IDbConnection db, string table, string column, string columnDefinition)
        {
            var existingColumns = db.Query<string>(
                    $"SELECT name FROM pragma_table_info('{table}')")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!existingColumns.Contains(column))
                db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}");
        }


        // ──────────────────────────────────────────────────────────────────────
        // NEXT PURCHASE NUMBER
        // ──────────────────────────────────────────────────────────────────────
        public int GetNextPurchaseNo()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int max = db.ExecuteScalar<int?>(
                    "SELECT MAX(purchase_no) FROM purchase_summary_details") ?? 0;
                return max + 1;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // VENDOR / CUSTOMER LOOKUP
        // ──────────────────────────────────────────────────────────────────────
        public List<VendorCustomer> GetVendors()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<VendorCustomer>(
                    "SELECT * FROM vendor_customer_master WHERE category IN ('Vendor', 'Both') ORDER BY customer_name")
                    .ToList();
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // ITEM LOOKUPS
        // ──────────────────────────────────────────────────────────────────────
        public List<ItemLookup> GetItemLookups()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<ItemLookup>($@"
                    SELECT {ItemLookupSelectColumns}
                    FROM   item_master
                    ORDER  BY item_name").ToList();
            }
        }

        public ItemLookup GetItemByCode(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<ItemLookup>($@"
                    SELECT {ItemLookupSelectColumns}
                    FROM   item_master
                    WHERE  item_code = @Code",
                    new { Code = itemCode });
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // ACCOUNT HEAD LOOKUPS (for expenses grid)
        // ──────────────────────────────────────────────────────────────────────
        public List<AcHeadLookup> GetAcHeadLookups()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AcHeadLookup>($@"
                    SELECT {AcHeadSelectColumns}
                    FROM   ac_head_master
                    ORDER  BY ac_head_name").ToList();
            }
        }

        /// <summary>Account heads under the reserved Banks category — used by
        /// the money-transfer "To Card" combo box. Delegates to the shared
        /// BillPro.DataAccess.BankAccountLookup so every entry screen's bank
        /// combo draws from the exact same query.</summary>
        public List<AcHeadLookup> GetBankAcHeads() => new AccountHeadRepository().GetBankAcHeadLookups();

        // ──────────────────────────────────────────────────────────────────────
        // SAVE (INSERT) — wrapped in a single SQLite transaction
        // ──────────────────────────────────────────────────────────────────────
        public void Save(
            PurchaseSummary summary,
            IList<PurchaseItemDetail> items,
            IList<PurchaseExternalExpense> expenses,
            bool updatePurchaseRate,
            string currentUser)
        {
            CalculateLandingCosts(summary, items, expenses);

            summary.CreatedBy = currentUser;
            summary.CreatedDate = DateTime.Now;

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        InsertSummary(db, tx, summary, includeModifiedColumns: false);
                        InsertItemsAndSyncMaster(db, tx, summary, items, updatePurchaseRate);
                        InsertExpenses(db, tx, summary, expenses);

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            PostToAccounts(summary, items, currentUser);
        }

        // ──────────────────────────────────────────────────────────────────────
        // UPDATE — delete old records then re-insert inside one transaction
        // ──────────────────────────────────────────────────────────────────────
        public void Update(
            PurchaseSummary summary,
            IList<PurchaseItemDetail> items,
            IList<PurchaseExternalExpense> expenses,
            bool updatePurchaseRate,
            string currentUser,
            string originalCreatedBy,
            DateTime originalCreatedDate)
        {
            CalculateLandingCosts(summary, items, expenses);

            summary.CreatedBy = originalCreatedBy;
            summary.CreatedDate = originalCreatedDate;
            summary.LastModifiedBy = currentUser;
            summary.LastModifiedDate = DateTime.Now;

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        int pNo = summary.PurchaseNo;

                        // Reverse the ledger effect of the OLD item rows before
                        // wiping them — same pattern as OpeningStockRepository.Update:
                        // delete only the specific ledger rows this purchase created
                        // (via their stored trans_id), never touch the batches
                        // themselves, so other documents referencing those batches
                        // stay intact.
                        var oldTransIds = db.Query<int?>(
                            "SELECT trans_id FROM purchase_item_details WHERE purchase_no = @pNo AND trans_id IS NOT NULL",
                            new { pNo }, tx).ToList();

                        foreach (var transId in oldTransIds)
                            if (transId.HasValue)
                                _stockRepo.DeleteLedgerEntry(db, tx, transId.Value);

                        db.Execute("DELETE FROM purchase_summary_details   WHERE purchase_no = @pNo", new { pNo }, tx);
                        db.Execute("DELETE FROM purchase_item_details      WHERE purchase_no = @pNo", new { pNo }, tx);
                        db.Execute("DELETE FROM purchase_external_expenses WHERE purchase_no = @pNo", new { pNo }, tx);

                        InsertSummary(db, tx, summary, includeModifiedColumns: true);
                        InsertItemsAndSyncMaster(db, tx, summary, items, updatePurchaseRate);
                        InsertExpenses(db, tx, summary, expenses);

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            PostToAccounts(summary, items, currentUser);
        }

        // ──────────────────────────────────────────────────────────────────────
        // SHARED WRITE HELPERS (used by both Save and Update)
        // ──────────────────────────────────────────────────────────────────────
        private static void InsertSummary(
            IDbConnection db, IDbTransaction tx, PurchaseSummary summary, bool includeModifiedColumns)
        {
            string modifiedColumns = includeModifiedColumns
                ? ", last_modified_by, last_modified_date" : "";
            string modifiedValues = includeModifiedColumns
                ? ", @LastModifiedBy, @LastModifiedDate" : "";

            db.Execute($@"
                INSERT INTO purchase_summary_details (
                    purchase_no, purchase_date, bill_no, bill_date,
                    purchase_mode, vendor_code, vendor_name,
                    address1, address2, address3, gst_no, phone, mobile,
                    state, state_code,
                    price_amount, tax_total, grand_total,
                    cash_discount, rounded_amount, net_total,
                    to_card_amount, bank_head_code, bank_head_name,
                    to_cash_amount, to_credit_amount,
                    created_by, created_date{modifiedColumns}
                ) VALUES (
                    @PurchaseNo, @PurchaseDate, @BillNo, @BillDate,
                    @PurchaseMode, @VendorCode, @VendorName,
                    @Address1, @Address2, @Address3, @GstNo, @Phone, @Mobile,
                    @State, @StateCode,
                    @PriceAmount, @TaxTotal, @GrandTotal,
                    @CashDiscount, @RoundedAmount, @NetTotal,
                    @ToCardAmount, @BankHeadCode, @BankHeadName,
                    @ToCashAmount, @ToCreditAmount,
                    @CreatedBy, @CreatedDate{modifiedValues}
                )",
                new
                {
                    summary.PurchaseNo,
                    PurchaseDate = summary.PurchaseDate.ToString("yyyy-MM-dd"),
                    summary.BillNo,
                    BillDate = summary.BillDate.ToString("yyyy-MM-dd"),
                    summary.PurchaseMode,
                    summary.VendorCode,
                    summary.VendorName,
                    summary.Address1,
                    summary.Address2,
                    summary.Address3,
                    summary.GstNo,
                    summary.Phone,
                    summary.Mobile,
                    summary.State,
                    summary.StateCode,
                    summary.PriceAmount,
                    summary.TaxTotal,
                    summary.GrandTotal,
                    summary.CashDiscount,
                    summary.RoundedAmount,
                    summary.NetTotal,
                    summary.ToCardAmount,
                    summary.BankHeadCode,
                    summary.BankHeadName,
                    summary.ToCashAmount,
                    summary.ToCreditAmount,
                    summary.CreatedBy,
                    CreatedDate = summary.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                    summary.LastModifiedBy,
                    LastModifiedDate = summary.LastModifiedDate?.ToString("yyyy-MM-dd HH:mm:ss")
                }, tx);
        }

        private void InsertItemsAndSyncMaster(
            IDbConnection db, IDbTransaction tx,
            PurchaseSummary summary, IList<PurchaseItemDetail> items, bool updatePurchaseRate)
        {
            string purchaseDate = summary.PurchaseDate.ToString("yyyy-MM-dd");

            foreach (var item in items)
            {
                // ── Stock ledger: upsert batch (item_code+mrp), then post a
                // PURCHASE ledger row for this quantity. sales_rate is passed
                // as null — Purchase Entry doesn't collect one, so an existing
                // batch's sales rate is left untouched (see GetOrCreateBatch).
                int batchId = _stockRepo.GetOrCreateBatch(
                    db, tx, item.ItemCode, (double)item.Mrp,
                    purchaseRate: (double)item.Rate,
                    salesRate: null);

                int transId = _stockRepo.InsertLedger(
                    db, tx, transDate: purchaseDate, transType: "PURCHASE",
                    batchId: batchId, qtyIn: (double)item.Quantity, qtyOut: 0);

                db.Execute(@"
                    INSERT INTO purchase_item_details (
                        purchase_no, purchase_date, item_code, item_name, hsn_code,
                        mrp, rate, discount_rate, discount, quantity,
                        tax_rate, tax_amount,
                        price, landing_cost, landing_cost_stock,
                        batch_id, trans_id
                    ) VALUES (
                        @PurchaseNo, @PurchaseDate, @ItemCode, @ItemName, @HsnCode,
                        @Mrp, @Rate, @DiscountRate, @Discount, @Quantity,
                        @TaxRate, @TaxAmount,
                        @Price, @LandingCost, @LandingCostStock,
                        @BatchId, @TransId
                    )",
                    new
                    {
                        summary.PurchaseNo,
                        PurchaseDate = purchaseDate,
                        item.ItemCode,
                        item.ItemName,
                        item.HsnCode,
                        item.Mrp,
                        item.Rate,
                        item.DiscountRate,
                        item.Discount,
                        item.Quantity,
                        item.TaxRate,
                        item.TaxAmount,
                        item.Price,
                        item.LandingCost,
                        item.LandingCostStock,
                        BatchId = batchId,
                        TransId = transId
                    }, tx);

                if (updatePurchaseRate)
                {
                    db.Execute(@"
                        UPDATE item_master
                        SET    unit_price_purchase = @Rate,
                               mrp                 = @Mrp
                        WHERE  item_code = @ItemCode",
                        new { item.Rate, item.Mrp, item.ItemCode }, tx);
                }
                else
                {
                    // Purchase rate left unchanged → still keep the item's
                    // running average cost up to date so stock valuation
                    // reflects what this batch actually landed at.
                    db.Execute(@"
                        UPDATE item_master
                        SET    average_cost = @LandingCost
                        WHERE  item_code = @ItemCode",
                        new { item.LandingCost, item.ItemCode }, tx);
                }
            }
        }

        private static void InsertExpenses(
            IDbConnection db, IDbTransaction tx,
            PurchaseSummary summary, IList<PurchaseExternalExpense> expenses)
        {
            string purchaseDate = summary.PurchaseDate.ToString("yyyy-MM-dd");

            foreach (var exp in expenses)
            {
                db.Execute(@"
                    INSERT INTO purchase_external_expenses (
                        purchase_no, purchase_date, ac_head_code, narration,
                        credit, debit,
                        external_tax_rate, external_tax
                    ) VALUES (
                        @PurchaseNo, @PurchaseDate, @AcHeadCode, @Narration,
                        @Credit, @Debit,
                        @ExternalTaxRate, @ExternalTax
                    )",
                    new
                    {
                        summary.PurchaseNo,
                        PurchaseDate = purchaseDate,
                        exp.AcHeadCode,
                        exp.Narration,
                        exp.Credit,
                        exp.Debit,
                        exp.ExternalTaxRate,
                        exp.ExternalTax
                    }, tx);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // DELETE (Cancel bill)
        // ──────────────────────────────────────────────────────────────────────
        public void Delete(int purchaseNo)
        {
            // Grab the ref/date before the row is physically deleted — the
            // accounts-side reversal needs them and purchase_summary_details
            // won't exist to look them up from afterward (Purchase hard-
            // deletes, unlike the soft-delete Sales/Return entries use).
            PurchaseSummary summaryForReversal = GetSummaryByNo(purchaseNo);

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        var transIds = db.Query<int?>(
                            "SELECT trans_id FROM purchase_item_details WHERE purchase_no = @pNo AND trans_id IS NOT NULL",
                            new { pNo = purchaseNo }, tx).ToList();

                        foreach (var transId in transIds)
                            if (transId.HasValue)
                                _stockRepo.DeleteLedgerEntry(db, tx, transId.Value);

                        db.Execute("DELETE FROM purchase_summary_details   WHERE purchase_no = @pNo", new { pNo = purchaseNo }, tx);
                        db.Execute("DELETE FROM purchase_item_details      WHERE purchase_no = @pNo", new { pNo = purchaseNo }, tx);
                        db.Execute("DELETE FROM purchase_external_expenses WHERE purchase_no = @pNo", new { pNo = purchaseNo }, tx);
                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
            }

            if (summaryForReversal != null)
            {
                try
                {
                    _financialSync.Reverse(TransactionType.Purchase, PurchaseVoucherRef(summaryForReversal),
                        summaryForReversal.PurchaseDate.ToString("yyyy-MM-dd"));
                }
                catch
                {
                    // Purchase is hard-deleted, so there's no row left to flag
                    // accounts_synced = 0 on — log this so a maintenance sweep
                    // (RefreshDaybookSummary / a "stale postings" report) can
                    // catch orphaned voucher_tax_postings for a deleted bill.
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // LOAD EXISTING BILL FOR EDIT
        // ──────────────────────────────────────────────────────────────────────
        public PurchaseSummary GetSummaryByNo(int purchaseNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<PurchaseSummary>($@"
                    SELECT {SummarySelectColumns}
                    FROM   purchase_summary_details
                    WHERE  purchase_no = @pNo",
                    new { pNo = purchaseNo });
            }
        }

        public List<PurchaseItemDetail> GetItemsByPurchaseNo(int purchaseNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<PurchaseItemDetail>(
                    "SELECT * FROM purchase_item_details WHERE purchase_no = @pNo",
                    new { pNo = purchaseNo }).ToList();
            }
        }

        public List<PurchaseExternalExpense> GetExpensesByPurchaseNo(int purchaseNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<PurchaseExternalExpense>(@"
                    SELECT e.id                AS Id,
                           e.purchase_no       AS PurchaseNo,
                           e.purchase_date     AS PurchaseDate,
                           e.ac_head_code      AS AcHeadCode,
                           e.narration         AS Narration,
                           e.credit            AS Credit,
                           e.debit             AS Debit,
                           e.external_tax_rate AS ExternalTaxRate,
                           e.external_tax      AS ExternalTax
                    FROM   purchase_external_expenses e
                    WHERE  e.purchase_no = @pNo",
                    new { pNo = purchaseNo }).ToList();
            }
        }

        // ac_head_name is not stored in purchase_external_expenses;
        // query the accounts DB for each code and stamp the name onto each row.
        public void ResolveAcHeadNames(IList<PurchaseExternalExpense> expenses)
        {
            if (expenses == null || expenses.Count == 0) return;

            var codes = expenses.Select(e => e.AcHeadCode).Distinct().ToList();

            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var lookup = db.Query<AcHeadLookup>($@"
                    SELECT {AcHeadSelectColumns}
                    FROM   ac_head_master
                    WHERE  ac_head_code IN @Codes",
                    new { Codes = codes })
                    .ToDictionary(a => a.AcHeadCode, a => a.AcHeadName);

                foreach (var exp in expenses)
                {
                    if (lookup.TryGetValue(exp.AcHeadCode, out var name))
                        exp.AcHeadName = name;
                }
            }
        }

        public List<PurchaseSummary> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<PurchaseSummary>($@"
                    SELECT {SummarySelectColumns}
                    FROM   purchase_summary_details
                    ORDER  BY purchase_no DESC").ToList();
            }
        }

        // Purchase Report / Item-wise Report / Settlement Report / Tax-Rate
        // Breakup all live in PurchaseRepository.reports.cs.

        // ──────────────────────────────────────────────────────────────────────
        // LANDING COST CALCULATION  (ported from VB6)
        // Formula:
        //   others_total  = rounded_amount − cash_discount + external_debit_total
        //   added_amt     = (others_total / price_total) × item_price
        //   landing_cost  = (item_price + added_amt) / quantity
        // ──────────────────────────────────────────────────────────────────────
        private static void CalculateLandingCosts(
            PurchaseSummary summary,
            IList<PurchaseItemDetail> items,
            IList<PurchaseExternalExpense> expenses)
        {
            if (!items.Any()) return;

            decimal externalDebitTotal = expenses.Sum(e => e.Debit);
            decimal othersTotal = summary.RoundedAmount
                                - summary.CashDiscount
                                + externalDebitTotal;

            decimal priceTotal = items.Sum(i => i.Price);
            if (priceTotal == 0) priceTotal = 1;

            foreach (var item in items)
            {
                decimal itemPrice = item.Price;
                decimal qty = item.Quantity == 0 ? 1 : item.Quantity;
                decimal addedAmt = othersTotal / priceTotal * itemPrice;

                item.LandingCost = Math.Round((itemPrice + addedAmt) / qty, 2);
                item.LandingCostStock = item.LandingCost;
            }
        }
    }
}