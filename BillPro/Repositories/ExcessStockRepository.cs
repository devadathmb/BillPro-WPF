using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    /// <summary>
    /// Excess Stock Entry — a pure stock-quantity correction (found extra
    /// stock: over-count, returned-but-unlogged goods, etc.). Never touches
    /// accounts, and never writes back to item_master.unit_price_purchase or
    /// item_batch_details.purchase_rate — PurchaseRate on each line is only
    /// a valuation snapshot for this entry.
    ///
    /// Deliberately separate from ShortStockRepository (own table pair, own
    /// entry-number sequence) even though today the code looks similar —
    /// Excess is allowed to create a brand-new batch on the fly (you can
    /// find excess stock of something that was never formally purchased),
    /// which Short Stock Entry must never do (see ShortStockRepository).
    /// Keeping them as separate classes means that difference — and any
    /// future one — doesn't need an isExcess branch threaded through every
    /// method.
    ///
    /// CANCEL vs DELETE: Delete removes the entry entirely (used to be the
    /// only option). CancelEntry (added alongside it) keeps the summary and
    /// detail rows for audit — it only reverses each line's ledger posting
    /// (same DeleteLedgerEntry call Delete already used) and flags the
    /// summary row IsCancelled = 1. Delete is left in place for callers that
    /// still want a hard remove; the UI now drives Cancel instead.
    /// </summary>
    public class ExcessStockRepository
    {
        private readonly StockMasterRepository _stockRepo = new StockMasterRepository();

        private const string TransType = "EXCESS_STOCK";
        private const string SummaryTable = "excess_stock_summary";
        private const string DetailTable = "excess_stock_item_details";
        private const string EntryNoColumn = "excess_entry_no";

        public static string FormatEntryNo(int entryNo) => "EX-" + entryNo;

        public void InitializeDatabase()
        {
            _stockRepo.InitializeDatabase();

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute($@"
                    CREATE TABLE IF NOT EXISTS {SummaryTable} (
                        {EntryNoColumn}    INTEGER PRIMARY KEY,
                        entry_date         TEXT,
                        remarks            TEXT,
                        created_by         TEXT,
                        created_date       TEXT,
                        last_modified_by   TEXT,
                        last_modified_date TEXT,
                        is_cancelled        INTEGER DEFAULT 0,
                        cancelled_by        TEXT,
                        cancelled_date      TEXT
                    );
                    CREATE TABLE IF NOT EXISTS {DetailTable} (
                        detail_id     INTEGER PRIMARY KEY AUTOINCREMENT,
                        {EntryNoColumn} INTEGER,
                        item_code     TEXT,
                        item_name     TEXT,
                        mrp           REAL DEFAULT 0,
                        purchase_rate REAL DEFAULT 0,
                        qty           REAL DEFAULT 0,
                        batch_id      INTEGER,
                        trans_id      INTEGER
                    );");

                // CREATE TABLE IF NOT EXISTS is a no-op on a table that
                // already existed before this version — it never adds the
                // new is_cancelled/cancelled_by/cancelled_date columns to an
                // existing summary table. Add them individually, ignoring
                // the "duplicate column" error SQLite raises when they're
                // already there (there's no ALTER TABLE ... ADD COLUMN IF
                // NOT EXISTS in SQLite).
                TryAddColumn(db, SummaryTable, "is_cancelled", "INTEGER DEFAULT 0");
                TryAddColumn(db, SummaryTable, "cancelled_by", "TEXT");
                TryAddColumn(db, SummaryTable, "cancelled_date", "TEXT");
            }
        }

        private void TryAddColumn(IDbConnection db, string table, string column, string definition)
        {
            try
            {
                db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
            }
            catch
            {
                // Column already exists — fine, nothing to do.
            }
        }

        public int GetNextEntryNo()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int max = db.ExecuteScalar<int?>($"SELECT MAX({EntryNoColumn}) FROM {SummaryTable}") ?? 0;
                return max + 1;
            }
        }

        /// <summary>Saves a brand-new entry (header + lines) and posts each
        /// line's quantity as qty_in on the stock ledger. Rates are never
        /// written back to the batch/item master.</summary>
        public int SaveNew(StockAdjustmentSummary summary, List<StockAdjustmentLine> lines, string user)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        int entryNo = GetNextEntryNo();
                        summary.EntryNo = entryNo;

                        db.Execute($@"
                            INSERT INTO {SummaryTable}
                                ({EntryNoColumn}, entry_date, remarks, created_by, created_date, is_cancelled)
                            VALUES (@EntryNo, @EntryDate, @Remarks, @User, @Now, 0)",
                            new
                            {
                                summary.EntryNo,
                                summary.EntryDate,
                                summary.Remarks,
                                User = user,
                                Now = DateTime.Now.ToString("s")
                            }, tx);

                        InsertLines(db, tx, entryNo, summary.EntryDate, lines);

                        tx.Commit();
                        return entryNo;
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>Updates an existing entry — reverses every old line's
        /// ledger posting (via its stored trans_id) before re-posting the
        /// new lines, same reverse-then-relink pattern used by
        /// ProductionRepository.UpdateExisting.</summary>
        public void UpdateExisting(StockAdjustmentSummary summary, List<StockAdjustmentLine> lines, string user)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        var oldTransIds = db.Query<int?>(
                            $"SELECT trans_id FROM {DetailTable} WHERE {EntryNoColumn} = @No AND trans_id IS NOT NULL",
                            new { No = summary.EntryNo }, tx).ToList();

                        foreach (var transId in oldTransIds)
                            if (transId.HasValue)
                                _stockRepo.DeleteLedgerEntry(db, tx, transId.Value);

                        db.Execute($"DELETE FROM {DetailTable} WHERE {EntryNoColumn} = @No",
                            new { No = summary.EntryNo }, tx);

                        db.Execute($@"
                            UPDATE {SummaryTable} SET
                                entry_date = @EntryDate,
                                remarks = @Remarks,
                                last_modified_by = @User,
                                last_modified_date = @Now
                            WHERE {EntryNoColumn} = @EntryNo",
                            new
                            {
                                summary.EntryDate,
                                summary.Remarks,
                                User = user,
                                Now = DateTime.Now.ToString("s"),
                                summary.EntryNo
                            }, tx);

                        InsertLines(db, tx, summary.EntryNo, summary.EntryDate, lines);

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

        /// <summary>Deletes an entry entirely — reverses every line's ledger
        /// posting first, then removes the detail and summary rows. Hard
        /// remove, no audit trail left behind — prefer CancelEntry from the
        /// UI; this stays available for callers that genuinely want the rows
        /// gone (e.g. a bulk data-cleanup utility).</summary>
        public void Delete(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        var transIds = db.Query<int?>(
                            $"SELECT trans_id FROM {DetailTable} WHERE {EntryNoColumn} = @No AND trans_id IS NOT NULL",
                            new { No = entryNo }, tx).ToList();

                        foreach (var transId in transIds)
                            if (transId.HasValue)
                                _stockRepo.DeleteLedgerEntry(db, tx, transId.Value);

                        db.Execute($"DELETE FROM {DetailTable} WHERE {EntryNoColumn} = @No", new { No = entryNo }, tx);
                        db.Execute($"DELETE FROM {SummaryTable} WHERE {EntryNoColumn} = @No", new { No = entryNo }, tx);

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

        /// <summary>Cancels an entry — reverses each line's stock effect by
        /// deleting its ledger posting (same call Delete uses), then flags
        /// the summary row IsCancelled = 1 with who/when. Unlike Delete,
        /// the summary and detail rows themselves are left in place for
        /// audit, and each line's trans_id is nulled out (its ledger
        /// posting no longer exists, so the stale id must not linger and be
        /// mistaken for a live posting by GetLines/UpdateExisting/Delete).
        /// Throws if the entry doesn't exist or is already cancelled, so
        /// the UI's confirmation dialog can't be raced into cancelling
        /// twice and reversing the same stock effect a second time.</summary>
        public void CancelEntry(int entryNo, string currentUser)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        int? alreadyCancelled = db.ExecuteScalar<int?>(
                            $"SELECT is_cancelled FROM {SummaryTable} WHERE {EntryNoColumn} = @No",
                            new { No = entryNo }, tx);

                        if (alreadyCancelled == null)
                            throw new InvalidOperationException($"Entry {FormatEntryNo(entryNo)} was not found.");

                        if (alreadyCancelled == 1)
                            throw new InvalidOperationException($"Entry {FormatEntryNo(entryNo)} is already cancelled.");

                        var transIds = db.Query<int?>(
                            $"SELECT trans_id FROM {DetailTable} WHERE {EntryNoColumn} = @No AND trans_id IS NOT NULL",
                            new { No = entryNo }, tx).ToList();

                        foreach (var transId in transIds)
                            if (transId.HasValue)
                                _stockRepo.DeleteLedgerEntry(db, tx, transId.Value);

                        db.Execute($"UPDATE {DetailTable} SET trans_id = NULL WHERE {EntryNoColumn} = @No",
                            new { No = entryNo }, tx);

                        db.Execute($@"
                            UPDATE {SummaryTable} SET
                                is_cancelled = 1,
                                cancelled_by = @User,
                                cancelled_date = @Now
                            WHERE {EntryNoColumn} = @No",
                            new { User = currentUser, Now = DateTime.Now.ToString("s"), No = entryNo }, tx);

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

        private void InsertLines(IDbConnection db, IDbTransaction tx, int entryNo, string entryDate, List<StockAdjustmentLine> lines)
        {
            foreach (var line in lines)
            {
                // purchaseRate/salesRate: null — an adjustment never changes
                // the batch's own rate, it only needs the batch to exist so
                // the ledger has somewhere to post against. Excess is
                // allowed to land on a brand-new MRP — GetOrCreateBatch
                // creates it on the fly, same as every other entry screen.
                int batchId = _stockRepo.GetOrCreateBatch(
                    db, tx, line.ItemCode, line.Mrp,
                    purchaseRate: null, salesRate: null);

                int transId = _stockRepo.InsertLedger(
                    db, tx, transDate: entryDate, transType: TransType, batchId: batchId,
                    qtyIn: line.Qty, qtyOut: 0);

                line.BatchId = batchId;
                line.TransId = transId;

                db.Execute($@"
                    INSERT INTO {DetailTable}
                        ({EntryNoColumn}, item_code, item_name, mrp, purchase_rate, qty, batch_id, trans_id)
                    VALUES
                        (@EntryNo, @ItemCode, @ItemName, @Mrp, @PurchaseRate, @Qty, @BatchId, @TransId)",
                    new
                    {
                        EntryNo = entryNo,
                        line.ItemCode,
                        line.ItemName,
                        line.Mrp,
                        line.PurchaseRate,
                        line.Qty,
                        line.BatchId,
                        line.TransId
                    }, tx);
            }
        }

        public StockAdjustmentSummary GetSummary(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var summary = db.QueryFirstOrDefault<StockAdjustmentSummary>(
                    $@"SELECT {EntryNoColumn} AS EntryNo, entry_date AS EntryDate, remarks AS Remarks,
                              created_by AS CreatedBy, created_date AS CreatedDate,
                              last_modified_by AS LastModifiedBy, last_modified_date AS LastModifiedDate,
                              COALESCE(is_cancelled, 0) AS IsCancelled,
                              cancelled_by AS CancelledBy, cancelled_date AS CancelledDate
                       FROM {SummaryTable} WHERE {EntryNoColumn} = @No",
                    new { No = entryNo });

                if (summary != null)
                    summary.FormattedEntryNo = FormatEntryNo(summary.EntryNo);

                return summary;
            }
        }

        public List<StockAdjustmentLine> GetLines(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var rows = db.Query<StockAdjustmentLine>(
                    $@"SELECT detail_id AS DetailId, item_code AS ItemCode, item_name AS ItemName,
                              CAST(mrp AS REAL) AS Mrp, CAST(purchase_rate AS REAL) AS PurchaseRate,
                              CAST(qty AS REAL) AS Qty, batch_id AS BatchId, trans_id AS TransId
                       FROM {DetailTable} WHERE {EntryNoColumn} = @No ORDER BY detail_id",
                    new { No = entryNo }).ToList();

                int sno = 1;
                foreach (var row in rows) row.SNo = sno++;
                return rows;
            }
        }

        /// <summary>List for the Display/search overlay — one row per entry,
        /// with TotalPurchaseRate/TotalQty rolled up from its lines, plus
        /// IsCancelled so the search grid can show cancelled entries.</summary>
        public List<StockAdjustmentSummary> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var summaries = db.Query<StockAdjustmentSummary>(
                    $@"SELECT {EntryNoColumn} AS EntryNo, entry_date AS EntryDate, remarks AS Remarks,
                              COALESCE(is_cancelled, 0) AS IsCancelled
                       FROM {SummaryTable} ORDER BY {EntryNoColumn} DESC").ToList();

                foreach (var s in summaries)
                {
                    s.FormattedEntryNo = FormatEntryNo(s.EntryNo);

                    var totals = db.QueryFirstOrDefault(
                        $@"SELECT CAST(COALESCE(SUM(purchase_rate),0) AS REAL) AS TotalPurchaseRate,
                                  CAST(COALESCE(SUM(qty),0) AS REAL) AS TotalQty
                           FROM {DetailTable} WHERE {EntryNoColumn} = @No",
                        new { No = s.EntryNo });
                    s.TotalPurchaseRate = totals?.TotalPurchaseRate ?? 0;
                    s.TotalQty = totals?.TotalQty ?? 0;
                }

                return summaries;
            }
        }

        /// <summary>Item-wise Excess Stock report — one row per adjustment
        /// line. Mirrors SalesReturnRepository.GetItemWiseReport's shape.</summary>
        public List<StockAdjustmentItemReportRow> GetItemWiseReport(DateTime fromDate, DateTime toDate, string search = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

                var rows = db.Query<StockAdjustmentItemReportRow>($@"
                    SELECT
                        s.{EntryNoColumn}  AS EntryNo,
                        s.entry_date       AS EntryDate,
                        s.remarks          AS Remarks,
                        d.item_code        AS ItemCode,
                        d.item_name        AS ItemName,
                        m.hsn_code         AS HsnCode,
                        b.batch_number     AS BatchNumber,
                        CAST(d.mrp AS REAL)           AS Mrp,
                        CAST(d.purchase_rate AS REAL) AS PurchaseRate,
                        CAST(d.qty AS REAL)           AS Qty
                    FROM {DetailTable} d
                    JOIN {SummaryTable} s ON s.{EntryNoColumn} = d.{EntryNoColumn}
                    LEFT JOIN item_master m ON m.item_code = d.item_code
                    LEFT JOIN item_batch_details b ON b.batch_id = d.batch_id
                    WHERE s.entry_date BETWEEN @From AND @To
                      AND (@Pattern IS NULL
                           OR d.item_name LIKE @Pattern
                           OR d.item_code LIKE @Pattern
                           OR s.remarks LIKE @Pattern
                           OR s.{EntryNoColumn} LIKE @Pattern)
                    ORDER BY s.entry_date, s.{EntryNoColumn}",
                    new { From = from, To = to, Pattern = pattern }).ToList();

                foreach (var r in rows)
                    r.FormattedEntryNo = FormatEntryNo(r.EntryNo);

                return rows;
            }
        }
    }
}