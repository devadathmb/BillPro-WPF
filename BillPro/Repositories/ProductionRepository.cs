using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public class ProductionRepository
    {
        private readonly StockMasterRepository _stockRepo = new StockMasterRepository();

        public void InitializeDatabase()
        {
            _stockRepo.InitializeDatabase();

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    CREATE TABLE IF NOT EXISTS production_summary_details (
                        production_no INTEGER PRIMARY KEY,
                        production_date TEXT,
                        item_code TEXT,
                        produced_qty REAL DEFAULT 0,
                        item_total REAL DEFAULT 0,
                        labour_charge REAL DEFAULT 0,
                        other_expense REAL DEFAULT 0,
                        grand_total REAL DEFAULT 0,
                        average_cost REAL DEFAULT 0,
                        created_by TEXT,
                        created_date TEXT,
                        last_modified_by TEXT,
                        last_modified_date TEXT
                    );
                    CREATE TABLE IF NOT EXISTS production_item_details (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        production_no INTEGER,
                        item_code TEXT,
                        item_name TEXT,
                        quantity REAL DEFAULT 0,
                        wastage REAL DEFAULT 0,
                        rate REAL DEFAULT 0,
                        amount REAL DEFAULT 0
                    );";
                db.Execute(sql);

                // Self-heal migration for installs created before the produced-item
                // stock/batch feature existed — same "brilliant idea" pattern as
                // purchase_item_details.batch_id/trans_id in PurchaseMasterRepository.
                AddColumnIfMissing(db, "production_summary_details", "mrp", "REAL DEFAULT 0");
                AddColumnIfMissing(db, "production_summary_details", "batch_id", "INTEGER");
                AddColumnIfMissing(db, "production_summary_details", "trans_id", "INTEGER");

                // Same pattern, one level down: each *consumed* raw-material row
                // also needs to remember which batch it was deducted from and
                // which ledger row that deduction created, so Update/Delete can
                // reverse it precisely instead of guessing.
                AddColumnIfMissing(db, "production_item_details", "batch_id", "INTEGER");
                AddColumnIfMissing(db, "production_item_details", "trans_id", "INTEGER");
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

        public int GetNextProductionNo()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int max = db.ExecuteScalar<int?>("SELECT MAX(production_no) FROM production_summary_details") ?? 0;
                return max + 1;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // MRP / BATCH LOOKUPS  (for the produced item's MRP combo box)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>Existing batches (MRP + current stock) for the given item — powers
        /// the "MRP / Batch" combo box's dropdown so a produced item can be added
        /// to an existing batch instead of always creating a new one.</summary>
        public IEnumerable<CurrentStockView> GetBatchesForItem(string itemCode)
        {
            return _stockRepo.GetCurrentStockByItem(itemCode);
        }

        /// <summary>Item master's default MRP — used to pre-fill the MRP box when the
        /// selected finished good has no batches yet (nothing produced/purchased so far).</summary>
        public double GetItemDefaultMrp(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<double?>(
                    "SELECT mrp FROM item_master WHERE item_code = @Code", new { Code = itemCode }) ?? 0;
            }
        }

        /// <summary>
        /// Picks the batch a *consumed* component is deducted from: the
        /// earliest existing batch for that item (lowest batch_id — a simple
        /// FIFO-by-creation-order approximation), so consumption draws down
        /// stock that already exists instead of fragmenting it. If the item
        /// has never been purchased/produced/opened before and has no batch
        /// at all yet, one is created at the item master's default MRP so the
        /// ledger has somewhere to post against — stock for it will simply go
        /// negative from here, same "Path B" allowance StockMasterRepository
        /// already documents for Purchase/Sales.
        /// </summary>
        private int GetOrCreateConsumptionBatchId(IDbConnection db, IDbTransaction tx, string itemCode)
        {
            int? existingBatchId = db.ExecuteScalar<int?>(
                "SELECT batch_id FROM item_batch_details WHERE item_code = @Code ORDER BY batch_id LIMIT 1",
                new { Code = itemCode }, tx);

            if (existingBatchId.HasValue) return existingBatchId.Value;

            double defaultMrp = db.ExecuteScalar<double?>(
                "SELECT mrp FROM item_master WHERE item_code = @Code", new { Code = itemCode }, tx) ?? 0;

            return _stockRepo.GetOrCreateBatch(db, tx, itemCode, defaultMrp);
        }

        /// <summary>Saves a brand-new production entry (header + item rows), posts the produced
        /// quantity into stock (item_batch_details/stock_transaction_details) at summary.Mrp,
        /// and returns the new production_no.</summary>
        public int SaveNew(ProductionSummary summary, List<ProductionItemDetail> items, string user)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        int productionNo = GetNextProductionNo();
                        summary.ProductionNo = productionNo;

                        // ── Stock ledger: upsert batch (item_code+mrp), then post a
                        // PRODUCTION ledger row for the produced quantity — same
                        // GetOrCreateBatch/InsertLedger pattern PurchaseMasterRepository
                        // uses. Cost basis for the batch is this run's average cost.
                        int batchId = _stockRepo.GetOrCreateBatch(
                            db, tx, summary.ItemCode, summary.Mrp,
                            purchaseRate: summary.AverageCost,
                            salesRate: null);

                        int transId = _stockRepo.InsertLedger(
                            db, tx, transDate: summary.ProductionDate, transType: "PRODUCTION",
                            batchId: batchId, qtyIn: summary.ProducedQty, qtyOut: 0);

                        summary.BatchId = batchId;
                        summary.TransId = transId;

                        db.Execute(@"
                            INSERT INTO production_summary_details
                                (production_no, production_date, item_code, produced_qty, item_total,
                                 labour_charge, other_expense, grand_total, average_cost, mrp, batch_id, trans_id,
                                 created_by, created_date)
                            VALUES
                                (@ProductionNo, @ProductionDate, @ItemCode, @ProducedQty, @ItemTotal,
                                 @LabourCharge, @OtherExpense, @GrandTotal, @AverageCost, @Mrp, @BatchId, @TransId,
                                 @User, @Now)",
                            new
                            {
                                summary.ProductionNo,
                                summary.ProductionDate,
                                summary.ItemCode,
                                summary.ProducedQty,
                                summary.ItemTotal,
                                summary.LabourCharge,
                                summary.OtherExpense,
                                summary.GrandTotal,
                                summary.AverageCost,
                                summary.Mrp,
                                summary.BatchId,
                                summary.TransId,
                                User = user,
                                Now = DateTime.Now.ToString("s")
                            }, tx);

                        InsertItemRows(db, tx, productionNo, summary.ProductionDate, items);

                        // Keep item_master's purchase cost in sync with what this production run actually cost —
                        // same behaviour as the legacy VB6 system.
                        db.Execute(@"UPDATE item_master SET unit_price_purchase = @Cost WHERE item_code = @Code",
                            new { Cost = summary.AverageCost, Code = summary.ItemCode }, tx);

                        tx.Commit();
                        return productionNo;
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>Updates an existing production entry (header + item rows). Reverses the
        /// old PRODUCTION ledger posting and re-posts against summary.Mrp (which may be a
        /// different batch than before), the same reverse-then-relink pattern
        /// PurchaseMasterRepository.Update uses for purchase_item_details.</summary>
        public void UpdateExisting(ProductionSummary summary, List<ProductionItemDetail> items, string user)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        // Reverse the OLD ledger row for this production entry before
                        // posting the new one — never touch the batch itself, only the
                        // specific ledger row this entry owns (via its stored trans_id),
                        // so other documents referencing that batch stay intact.
                        int? oldTransId = db.ExecuteScalar<int?>(
                            "SELECT trans_id FROM production_summary_details WHERE production_no = @No",
                            new { No = summary.ProductionNo }, tx);

                        if (oldTransId.HasValue)
                            _stockRepo.DeleteLedgerEntry(db, tx, oldTransId.Value);

                        // Same reversal, one level down: every OLD *consumed* item row
                        // posted its own qty_out ledger entry (via its own trans_id) —
                        // reverse each of those too before the rows themselves are
                        // deleted below, otherwise their stock deduction would be
                        // double-counted once the new rows re-post their own.
                        var oldItemTransIds = db.Query<int?>(
                            "SELECT trans_id FROM production_item_details WHERE production_no = @No AND trans_id IS NOT NULL",
                            new { No = summary.ProductionNo }, tx).ToList();

                        foreach (var oldItemTransId in oldItemTransIds)
                            if (oldItemTransId.HasValue)
                                _stockRepo.DeleteLedgerEntry(db, tx, oldItemTransId.Value);

                        int batchId = _stockRepo.GetOrCreateBatch(
                            db, tx, summary.ItemCode, summary.Mrp,
                            purchaseRate: summary.AverageCost,
                            salesRate: null);

                        int transId = _stockRepo.InsertLedger(
                            db, tx, transDate: summary.ProductionDate, transType: "PRODUCTION",
                            batchId: batchId, qtyIn: summary.ProducedQty, qtyOut: 0);

                        summary.BatchId = batchId;
                        summary.TransId = transId;

                        db.Execute(@"
                            UPDATE production_summary_details SET
                                production_date = @ProductionDate,
                                item_code = @ItemCode,
                                produced_qty = @ProducedQty,
                                item_total = @ItemTotal,
                                labour_charge = @LabourCharge,
                                other_expense = @OtherExpense,
                                grand_total = @GrandTotal,
                                average_cost = @AverageCost,
                                mrp = @Mrp,
                                batch_id = @BatchId,
                                trans_id = @TransId,
                                last_modified_by = @User,
                                last_modified_date = @Now
                            WHERE production_no = @ProductionNo",
                            new
                            {
                                summary.ProductionDate,
                                summary.ItemCode,
                                summary.ProducedQty,
                                summary.ItemTotal,
                                summary.LabourCharge,
                                summary.OtherExpense,
                                summary.GrandTotal,
                                summary.AverageCost,
                                summary.Mrp,
                                summary.BatchId,
                                summary.TransId,
                                User = user,
                                Now = DateTime.Now.ToString("s"),
                                summary.ProductionNo
                            }, tx);

                        db.Execute("DELETE FROM production_item_details WHERE production_no = @No",
                            new { No = summary.ProductionNo }, tx);

                        InsertItemRows(db, tx, summary.ProductionNo, summary.ProductionDate, items);

                        db.Execute(@"UPDATE item_master SET unit_price_purchase = @Cost WHERE item_code = @Code",
                            new { Cost = summary.AverageCost, Code = summary.ItemCode }, tx);

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

        private void InsertItemRows(IDbConnection db, IDbTransaction tx, int productionNo, string transDate, List<ProductionItemDetail> items)
        {
            foreach (var row in items)
            {
                // ── Stock ledger: deduct this component's consumption (Quantity +
                // Wastage — both physically leave stock) from whichever batch
                // GetOrCreateConsumptionBatchId picks for it, same
                // GetOrCreateBatch/InsertLedger shape used for the produced item,
                // just qty_out instead of qty_in.
                int batchId = GetOrCreateConsumptionBatchId(db, tx, row.Code);
                double consumedQty = row.Quantity + row.Wastage;

                int transId = _stockRepo.InsertLedger(
                    db, tx, transDate: transDate, transType: "PRODUCTION_CONSUME",
                    batchId: batchId, qtyIn: 0, qtyOut: consumedQty);

                row.BatchId = batchId;
                row.TransId = transId;

                db.Execute(@"
                    INSERT INTO production_item_details
                        (production_no, item_code, item_name, quantity, wastage, rate, amount, batch_id, trans_id)
                    VALUES
                        (@No, @Code, @ItemName, @Quantity, @Wastage, @Rate, @Amount, @BatchId, @TransId)",
                    new
                    {
                        No = productionNo,
                        row.Code,
                        row.ItemName,
                        row.Quantity,
                        row.Wastage,
                        row.Rate,
                        row.Amount,
                        row.BatchId,
                        row.TransId
                    }, tx);
            }
        }

        public ProductionSummary GetSummary(int productionNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<ProductionSummary>(@"
                    SELECT s.production_no AS ProductionNo, s.production_date AS ProductionDate,
                           s.item_code AS ItemCode, m.item_name AS ItemName,
                           s.produced_qty AS ProducedQty, s.item_total AS ItemTotal,
                           s.labour_charge AS LabourCharge, s.other_expense AS OtherExpense,
                           s.grand_total AS GrandTotal, s.average_cost AS AverageCost,
                           s.mrp AS Mrp, s.batch_id AS BatchId, s.trans_id AS TransId,
                           s.created_by AS CreatedBy, s.created_date AS CreatedDate
                    FROM production_summary_details s
                    LEFT JOIN item_master m ON m.item_code = s.item_code
                    WHERE s.production_no = @No", new { No = productionNo });
            }
        }

        public List<ProductionItemDetail> GetItems(int productionNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var rows = db.Query<ProductionItemDetail>(@"
                    SELECT item_code AS Code, item_name AS ItemName, quantity AS Quantity,
                           wastage AS Wastage, rate AS Rate, amount AS Amount,
                           batch_id AS BatchId, trans_id AS TransId
                    FROM production_item_details
                    WHERE production_no = @No
                    ORDER BY id", new { No = productionNo }).ToList();

                int n = 1;
                foreach (var r in rows) r.SNo = n++;
                return rows;
            }
        }

        /// <summary>All saved production headers, for the Display/search overlay.</summary>
        public List<ProductionSummary> GetAllSummaries()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<ProductionSummary>(@"
                    SELECT s.production_no AS ProductionNo, s.production_date AS ProductionDate,
                           s.item_code AS ItemCode, m.item_name AS ItemName,
                           s.produced_qty AS ProducedQty, s.grand_total AS GrandTotal,
                           s.mrp AS Mrp
                    FROM production_summary_details s
                    LEFT JOIN item_master m ON m.item_code = s.item_code
                    ORDER BY s.production_no DESC").ToList();
            }
        }

        /// <summary>Deletes a production entry and reverses the stock it posted
        /// (the specific PRODUCTION ledger row this entry created), leaving the
        /// batch itself intact for any other document that may reference it.</summary>
        public void DeleteProduction(int productionNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        int? transId = db.ExecuteScalar<int?>(
                            "SELECT trans_id FROM production_summary_details WHERE production_no = @No",
                            new { No = productionNo }, tx);

                        if (transId.HasValue)
                            _stockRepo.DeleteLedgerEntry(db, tx, transId.Value);

                        // Reverse every consumed item's own qty_out ledger row too —
                        // otherwise deleting the entry would leave the raw-material
                        // deduction in place with nothing left pointing back to it.
                        var itemTransIds = db.Query<int?>(
                            "SELECT trans_id FROM production_item_details WHERE production_no = @No AND trans_id IS NOT NULL",
                            new { No = productionNo }, tx).ToList();

                        foreach (var itemTransId in itemTransIds)
                            if (itemTransId.HasValue)
                                _stockRepo.DeleteLedgerEntry(db, tx, itemTransId.Value);

                        db.Execute("DELETE FROM production_item_details WHERE production_no = @No", new { No = productionNo }, tx);
                        db.Execute("DELETE FROM production_summary_details WHERE production_no = @No", new { No = productionNo }, tx);
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
    }
}