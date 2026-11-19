using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // Shared low-level batch + ledger operations.
    // Used by OpeningStockRepository today, and will be reused by
    // PurchaseEntry / SalesEntry (including the negative-stock Path B flow).
    public partial class StockMasterRepository
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new();

        public void InitializeDatabase()
        {
            // Same guard as SalesRepository/SalesSeriesRepository — this has
            // no ALTER TABLE today, but CREATE TABLE/VIEW IF NOT EXISTS still
            // needs to touch the schema to check, so it's still a moment of
            // write contention if called from more than one place at once.
            lock (_initLock)
            {
                if (_initialized) return;

                using (IDbConnection db = DatabaseConfig.GetConnection())
                {
                    string sql = @"
                        CREATE TABLE IF NOT EXISTS item_batch_details (
                            batch_id        INTEGER PRIMARY KEY AUTOINCREMENT,
                            item_code       TEXT NOT NULL,
                            mrp             REAL NOT NULL DEFAULT 0,
                            purchase_rate   REAL DEFAULT 0,
                            sales_rate      REAL DEFAULT 0,
                            batch_number    TEXT,
                            expiry_date     TEXT
                        );

                        CREATE TABLE IF NOT EXISTS stock_transaction_details (
                            trans_id        INTEGER PRIMARY KEY AUTOINCREMENT,
                            trans_date      TEXT NOT NULL,
                            trans_type      TEXT NOT NULL,
                            batch_id        INTEGER NOT NULL,
                            qty_in          REAL DEFAULT 0,
                            qty_out         REAL DEFAULT 0,
                            godown_id       INTEGER
                        );

                        CREATE VIEW IF NOT EXISTS View_CurrentStock AS
                        SELECT
                            b.batch_id, b.item_code, b.mrp, b.purchase_rate, b.sales_rate,
                            b.batch_number, b.expiry_date,
                            COALESCE(SUM(t.qty_in), 0) - COALESCE(SUM(t.qty_out), 0) AS current_qty
                        FROM item_batch_details b
                        LEFT JOIN stock_transaction_details t ON b.batch_id = t.batch_id
                        GROUP BY b.batch_id;";
                    db.Execute(sql);
                }

                _initialized = true;
            }
        }

        /// <summary>
        /// Finds an existing batch for (item_code, mrp), or creates a new one.
        /// Must be called within an open connection + transaction.
        /// New MRP => new batch, always.
        ///
        /// purchaseRate/salesRate are nullable: pass null for whichever one the
        /// calling screen doesn't actually collect (e.g. Purchase Entry has no
        /// Sales Rate field) so an existing batch's other rate isn't silently
        /// zeroed out. On a brand-new batch, null just becomes 0.
        /// </summary>
        public int GetOrCreateBatch(IDbConnection db, IDbTransaction tx, string itemCode, double mrp,
            double? purchaseRate = null, double? salesRate = null, string batchNumber = null, string expiryDate = null)
        {
            int? existingId = db.ExecuteScalar<int?>(
                "SELECT batch_id FROM item_batch_details WHERE item_code = @ItemCode AND mrp = @Mrp",
                new { ItemCode = itemCode, Mrp = mrp }, tx);

            if (existingId.HasValue)
            {
                // Batch already exists for this item+MRP — keep it (so other
                // documents referencing this batch_id stay intact), but refresh
                // whichever rate(s) the caller actually knows. A null here means
                // "this screen doesn't track that rate — leave it as-is".
                var setClauses = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("BatchId", existingId.Value);

                if (purchaseRate.HasValue)
                {
                    setClauses.Add("purchase_rate = @PurchaseRate");
                    parameters.Add("PurchaseRate", purchaseRate.Value);
                }
                if (salesRate.HasValue)
                {
                    setClauses.Add("sales_rate = @SalesRate");
                    parameters.Add("SalesRate", salesRate.Value);
                }

                if (setClauses.Count > 0)
                {
                    db.Execute($"UPDATE item_batch_details SET {string.Join(", ", setClauses)} WHERE batch_id = @BatchId",
                        parameters, tx);
                }

                return existingId.Value;
            }

            // A brand-new batch with no explicit rate (e.g. Production
            // Consumption, which doesn't collect a purchase/sales rate at
            // all) used to fall back to a hard 0 here — which is what made
            // Current Stock / Individual Stock reports show Purchase Rate
            // and Amount as 0 for an item whose very first batch happened
            // to be created that way, even though the item's own registered
            // rate (Item Registration) was non-zero. Falling back to
            // item_master's own rates instead means a freshly-registered
            // item still reports sensibly until a real purchase/opening
            // entry supplies its own batch-specific rate.
            double defaultPurchaseRate = 0, defaultSalesRate = 0;
            if (!purchaseRate.HasValue || !salesRate.HasValue)
            {
                var itemDefaults = db.QueryFirstOrDefault<ItemDefaultRates>(
                    "SELECT unit_price_purchase AS UnitPricePurchase, unit_price_sales AS UnitPriceSales FROM item_master WHERE item_code = @ItemCode",
                    new { ItemCode = itemCode }, tx);
                if (itemDefaults != null)
                {
                    defaultPurchaseRate = itemDefaults.UnitPricePurchase;
                    defaultSalesRate = itemDefaults.UnitPriceSales;
                }
            }

            db.Execute(@"INSERT INTO item_batch_details
                            (item_code, mrp, purchase_rate, sales_rate, batch_number, expiry_date)
                         VALUES (@ItemCode, @Mrp, @PurchaseRate, @SalesRate, @BatchNumber, @ExpiryDate)",
                new
                {
                    ItemCode = itemCode,
                    Mrp = mrp,
                    PurchaseRate = purchaseRate ?? defaultPurchaseRate,
                    SalesRate = salesRate ?? defaultSalesRate,
                    BatchNumber = batchNumber,
                    ExpiryDate = expiryDate
                }, tx);

            return db.ExecuteScalar<int>("SELECT last_insert_rowid()", transaction: tx);
        }

        // Small projection for the item_master rate lookup above — kept
        // private to this file since nothing else needs it.
        private class ItemDefaultRates
        {
            public double UnitPricePurchase { get; set; }
            public double UnitPriceSales { get; set; }
        }

        /// <summary>
        /// Inserts a ledger row and returns the new trans_id.
        /// Must be called within an open connection + transaction.
        /// </summary>
        public int InsertLedger(IDbConnection db, IDbTransaction tx, string transDate, string transType,
            int batchId, double qtyIn = 0, double qtyOut = 0, int? godownId = null)
        {
            db.Execute(@"INSERT INTO stock_transaction_details
                            (trans_date, trans_type, batch_id, qty_in, qty_out, godown_id)
                         VALUES (@TransDate, @TransType, @BatchId, @QtyIn, @QtyOut, @GodownId)",
                new
                {
                    TransDate = transDate,
                    TransType = transType,
                    BatchId = batchId,
                    QtyIn = qtyIn,
                    QtyOut = qtyOut,
                    GodownId = godownId
                }, tx);

            return db.ExecuteScalar<int>("SELECT last_insert_rowid()", transaction: tx);
        }

        public void DeleteLedgerEntry(IDbConnection db, IDbTransaction tx, int transId)
        {
            db.Execute("DELETE FROM stock_transaction_details WHERE trans_id = @Id", new { Id = transId }, tx);
        }

        public IEnumerable<CurrentStockView> GetCurrentStockByItem(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<CurrentStockView>(
                    "SELECT * FROM View_CurrentStock WHERE item_code = @Code ORDER BY mrp",
                    new { Code = itemCode });
            }
        }

        public double GetTotalStock(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<double?>(
                    "SELECT SUM(current_qty) FROM View_CurrentStock WHERE item_code = @Code",
                    new { Code = itemCode }) ?? 0;
            }
        }

        /// <summary>
        /// Current Stock report — one row per batch, quantity computed "as of"
        /// asOfDate (only ledger rows with trans_date &lt;= asOfDate count, so
        /// this can be used to reconstruct stock on any past date, not just
        /// today). Company/category are optional filters; null/empty means
        /// "all".
        ///
        /// Every stockable item (item_master.category IN Finished Goods / Raw
        /// Material / Consumables — i.e. anything that ISN'T category 3,
        /// Non-Stockable) is always included, even if it has never been
        /// purchased/produced/sold (no batch at all yet) or its current
        /// balance is exactly zero — the LEFT JOIN from item_master (instead
        /// of starting at item_batch_details) is what makes a batch-less item
        /// still produce a row, and there's deliberately no "HAVING CurrentQty
        /// &lt;&gt; 0" filter, so zero and negative balances both surface —
        /// negative in particular is exactly the kind of thing this report
        /// needs to expose (over-consumption / over-sale), not hide.
        /// Non-Stockable items (category 3) are excluded outright: they were
        /// never meant to carry a stock balance in the first place.
        /// </summary>
        public List<CurrentStockReportRow> GetCurrentStockReport(
            DateTime asOfDate, string companyCode = null, string categoryCode = null, string itemSearch = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string asOf = asOfDate.ToString("yyyy-MM-dd");
                string itemPattern = string.IsNullOrWhiteSpace(itemSearch) ? null : "%" + itemSearch.Trim() + "%";

                return db.Query<CurrentStockReportRow>(@"
                    SELECT
                        COALESCE(b.batch_id, 0)           AS BatchId,
                        m.item_code                       AS ItemCode,
                        m.item_name                       AS ItemName,
                        COALESCE(b.batch_number, '')      AS BatchNumber,
                        COALESCE(co.company_name, '')     AS CompanyName,
                        COALESCE(cat.item_category_name, '') AS CategoryName,
                        CAST(COALESCE(b.mrp, 0)           AS REAL) AS Mrp,
                        CAST(COALESCE(b.sales_rate, 0)    AS REAL) AS SalesRate,
                        CAST(COALESCE(b.purchase_rate, 0) AS REAL) AS PurchaseRate,
                        CAST(
                            COALESCE(SUM(CASE WHEN t.trans_date <= @AsOf THEN t.qty_in  ELSE 0 END), 0)
                          - COALESCE(SUM(CASE WHEN t.trans_date <= @AsOf THEN t.qty_out ELSE 0 END), 0)
                        AS REAL) AS CurrentQty
                    FROM item_master m
                    LEFT JOIN item_batch_details b    ON b.item_code = m.item_code
                    LEFT JOIN company_master co        ON co.company_code = m.company_code
                    LEFT JOIN item_category_master cat ON cat.item_category_code = m.item_category_code
                    LEFT JOIN stock_transaction_details t ON t.batch_id = b.batch_id
                    WHERE m.category <> 3   -- exclude Non-Stockable items entirely
                      AND (@CompanyCode IS NULL OR m.company_code = @CompanyCode)
                      AND (@CategoryCode IS NULL OR m.item_category_code = @CategoryCode)
                      AND (@ItemPattern IS NULL OR m.item_name LIKE @ItemPattern)
                    GROUP BY m.item_code, b.batch_id
                    ORDER BY m.item_name, b.mrp",
                    new
                    {
                        AsOf = asOf,
                        CompanyCode = string.IsNullOrWhiteSpace(companyCode) ? null : companyCode,
                        CategoryCode = string.IsNullOrWhiteSpace(categoryCode) ? null : categoryCode,
                        ItemPattern = itemPattern
                    }).ToList();
            }
        }
    }
}