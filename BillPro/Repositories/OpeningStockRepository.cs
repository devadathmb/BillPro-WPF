using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public class OpeningStockRepository
    {
        private readonly StockMasterRepository _stockRepo = new StockMasterRepository();

        public void InitializeDatabase()
        {
            _stockRepo.InitializeDatabase();

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    CREATE TABLE IF NOT EXISTS opening_stock_master (
                        entry_no            TEXT PRIMARY KEY,
                        entry_date          TEXT,
                        remarks             TEXT,
                        created_by          TEXT,
                        created_date        TEXT,
                        last_modified_by    TEXT,
                        last_modified_date  TEXT
                    );

                    CREATE TABLE IF NOT EXISTS opening_stock_details (
                        detail_id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        entry_no            TEXT NOT NULL,
                        item_code           TEXT NOT NULL,
                        batch_id            INTEGER NOT NULL,
                        trans_id            INTEGER NOT NULL,
                        qty                 REAL NOT NULL,
                        created_by          TEXT,
                        created_date        TEXT,
                        last_modified_by    TEXT,
                        last_modified_date  TEXT
                    );";
                db.Execute(sql);
            }
        }

        /// <summary>
        /// Batch-wise Opening Stock report: every OPENING ledger movement for a
        /// batch (item_code+mrp) summed into one row. Multiple opening-stock
        /// entries against the same batch collapse into a single total.
        /// Optional date range filters on the ledger's trans_date.
        /// </summary>
        public List<OpeningStockReportRow> GetOpeningStockReport(string itemSearch = null /*, DateTime? fromDate = null, DateTime? toDate = null */)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    SELECT
                        b.batch_id                        AS BatchId,
                        b.item_code                        AS ItemCode,
                        m.item_name                        AS ItemName,
                        b.mrp                               AS Mrp,
                        b.sales_rate                        AS SalesRate,
                        b.purchase_rate                     AS PurchaseRate,
                        SUM(t.qty_in - t.qty_out)           AS TotalQty,
                        SUM(t.qty_in - t.qty_out) * b.purchase_rate AS Amount,
                        COUNT(*)                            AS EntryCount,
                        MIN(t.trans_date)                   AS FirstEntryDate,
                        MAX(t.trans_date)                   AS LastEntryDate
                    FROM stock_transaction_details t
                    JOIN item_batch_details b ON b.batch_id = t.batch_id
                    JOIN item_master m ON m.item_code = b.item_code
                    WHERE t.trans_type = 'OPENING'";

                var args = new DynamicParameters();

                // ── FINANCIAL YEAR FILTER (not implemented yet) ──────────────
                // Once a Financial Year setting exists, restrict the report to
                // opening-stock movements from the FY start date onward instead
                // of showing everything since inception. Uncomment and wire up
                // FinancialYearRepository.GetCurrentFyStartDate() (or similar)
                // when that feature is built.
                //
                // DateTime fyStart = _financialYearRepo.GetCurrentFyStartDate();
                // sql += " AND t.trans_date >= @FyStart";
                // args.Add("FyStart", fyStart.ToString("s"));
                //
                // if (fromDate.HasValue)
                // {
                //     sql += " AND t.trans_date >= @From";
                //     args.Add("From", fromDate.Value.Date.ToString("s"));
                // }
                // if (toDate.HasValue)
                // {
                //     sql += " AND t.trans_date < @To";
                //     args.Add("To", toDate.Value.Date.AddDays(1).ToString("s"));
                // }

                if (!string.IsNullOrWhiteSpace(itemSearch))
                {
                    sql += " AND (m.item_name LIKE @Search OR m.item_code LIKE @Search)";
                    args.Add("Search", "%" + itemSearch.Trim() + "%");
                }

                sql += @"
                    GROUP BY b.batch_id
                    ORDER BY m.item_name, b.mrp";

                return db.Query<OpeningStockReportRow>(sql, args).ToList();
            }
        }

        public string GetNextEntryNo()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int max = db.ExecuteScalar<int?>("SELECT MAX(CAST(entry_no AS INTEGER)) FROM opening_stock_master") ?? 0;
                return (max + 1).ToString();
            }
        }

        public bool EntryExists(string entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM opening_stock_master WHERE entry_no = @No",
                    new { No = entryNo }) > 0;
            }
        }

        /// <summary>
        /// Saves a brand-new Opening Stock entry: header + all lines.
        /// For each line -> upsert batch (item_code+mrp) -> insert ledger row (OPENING) -> insert detail row.
        /// </summary>
        public void SaveNew(OpeningStockMaster header, List<OpeningStockLine> lines, string user)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        string now = DateTime.Now.ToString("s");

                        db.Execute(@"INSERT INTO opening_stock_master
                                        (entry_no, entry_date, remarks, created_by, created_date)
                                     VALUES (@EntryNo, @EntryDate, @Remarks, @CreatedBy, @CreatedDate)",
                            new { header.EntryNo, header.EntryDate, header.Remarks, CreatedBy = user, CreatedDate = now }, tx);

                        foreach (var line in lines)
                        {
                            InsertLine(db, tx, header.EntryNo, line, user, now);
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
        }

        /// <summary>
        /// Updates an existing entry. Old ledger rows tied to this entry are removed and
        /// fresh ones inserted (batches are never deleted, so other documents stay intact).
        /// </summary>
        public void Update(OpeningStockMaster header, List<OpeningStockLine> lines, string user)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        // 1. Remove old ledger rows that belong to this entry's existing details
                        var oldTransIds = db.Query<int>(
                            "SELECT trans_id FROM opening_stock_details WHERE entry_no = @No",
                            new { No = header.EntryNo }, tx).ToList();

                        foreach (int transId in oldTransIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, transId);

                        db.Execute("DELETE FROM opening_stock_details WHERE entry_no = @No",
                            new { No = header.EntryNo }, tx);

                        // 2. Update header
                        string now = DateTime.Now.ToString("s");
                        db.Execute(@"UPDATE opening_stock_master
                                     SET entry_date = @EntryDate, remarks = @Remarks,
                                         last_modified_by = @User, last_modified_date = @Now
                                     WHERE entry_no = @EntryNo",
                            new { header.EntryDate, header.Remarks, User = user, Now = now, header.EntryNo }, tx);

                        // 3. Re-insert fresh lines (batch upsert + new ledger + new detail)
                        foreach (var line in lines)
                        {
                            InsertLine(db, tx, header.EntryNo, line, user, now);
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
        }

        public void Delete(string entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        var transIds = db.Query<int>(
                            "SELECT trans_id FROM opening_stock_details WHERE entry_no = @No",
                            new { No = entryNo }, tx).ToList();

                        foreach (int transId in transIds)
                            _stockRepo.DeleteLedgerEntry(db, tx, transId);

                        db.Execute("DELETE FROM opening_stock_details WHERE entry_no = @No", new { No = entryNo }, tx);
                        db.Execute("DELETE FROM opening_stock_master WHERE entry_no = @No", new { No = entryNo }, tx);

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        public List<OpeningStockMaster> GetAllHeaders()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<OpeningStockMaster>(@"
                    SELECT entry_no AS EntryNo, entry_date AS EntryDate, remarks AS Remarks
                    FROM opening_stock_master
                    ORDER BY CAST(entry_no AS INTEGER) DESC").ToList();
            }
        }

        public OpeningStockMaster GetHeader(string entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<OpeningStockMaster>(
                    "SELECT entry_no AS EntryNo, entry_date AS EntryDate, remarks AS Remarks FROM opening_stock_master WHERE entry_no = @No",
                    new { No = entryNo });
            }
        }

        /// <summary>Full item list for the entry-line SmartComboBox (Code/Name/MRP/Sales Rate lookup).</summary>
        public List<ItemLookup> GetItemLookups()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<ItemLookup>(@"
                    SELECT
                        item_code            AS ItemCode,
                        item_name            AS ItemName,
                        hsn_code             AS HsnCode,
                        mrp                  AS Mrp,
                        unit_price_purchase  AS UnitPricePurchase,
                        unit_price_sales     AS UnitPriceSales,
                        tax_rate             AS TaxRate
                    FROM item_master
                    ORDER BY item_name").ToList();
            }
        }

        public ItemLookup GetItemByCode(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<ItemLookup>(@"
                    SELECT
                        item_code            AS ItemCode,
                        item_name            AS ItemName,
                        hsn_code             AS HsnCode,
                        mrp                  AS Mrp,
                        unit_price_purchase  AS UnitPricePurchase,
                        unit_price_sales     AS UnitPriceSales,
                        tax_rate             AS TaxRate
                    FROM item_master
                    WHERE item_code = @Code",
                    new { Code = itemCode });
            }
        }

        /// <summary>
        /// Current qty already recorded (across all trans types) for a given item+MRP batch.
        /// Used to soft-warn the user before adding a duplicate opening-stock line.
        /// Returns 0 if no batch exists yet for that item+MRP.
        /// </summary>
        public double GetCurrentQtyForItemMrp(string itemCode, decimal mrp)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<double?>(@"
                    SELECT v.current_qty
                    FROM View_CurrentStock v
                    JOIN item_batch_details b ON b.batch_id = v.batch_id
                    WHERE b.item_code = @ItemCode AND b.mrp = @Mrp",
                    new { ItemCode = itemCode, Mrp = (double)mrp }) ?? 0;
            }
        }

        public List<OpeningStockLine> GetLines(string entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<OpeningStockLine>(@"
                    SELECT
                        d.detail_id     AS DetailId,
                        d.item_code     AS ItemCode,
                        m.item_name     AS ItemName,
                        d.batch_id      AS BatchId,
                        d.trans_id      AS TransId,
                        b.mrp           AS Mrp,
                        d.qty           AS Qty,
                        b.sales_rate    AS SalesRate,
                        b.purchase_rate AS PurchaseRate
                    FROM opening_stock_details d
                    JOIN item_master m ON m.item_code = d.item_code
                    JOIN item_batch_details b ON b.batch_id = d.batch_id
                    WHERE d.entry_no = @No
                    ORDER BY d.detail_id",
                    new { No = entryNo }).ToList();
            }
        }

        private void InsertLine(IDbConnection db, IDbTransaction tx, string entryNo, OpeningStockLine line, string user, string now)
        {
            int batchId = _stockRepo.GetOrCreateBatch(
                db, tx, line.ItemCode, (double)line.Mrp,
                purchaseRate: (double)line.PurchaseRate,
                salesRate: (double)line.SalesRate);

            int transId = _stockRepo.InsertLedger(
                db, tx, transDate: now, transType: "OPENING",
                batchId: batchId, qtyIn: (double)line.Qty, qtyOut: 0);

            db.Execute(@"INSERT INTO opening_stock_details
                            (entry_no, item_code, batch_id, trans_id, qty, created_by, created_date)
                         VALUES (@EntryNo, @ItemCode, @BatchId, @TransId, @Qty, @CreatedBy, @CreatedDate)",
                new
                {
                    EntryNo = entryNo,
                    line.ItemCode,
                    BatchId = batchId,
                    TransId = transId,
                    line.Qty,
                    CreatedBy = user,
                    CreatedDate = now
                }, tx);
        }
    }
}