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
    // INDIVIDUAL STOCK REPORT — batch picker, transaction history (single
    // batch or every batch of an item combined), and source-document
    // resolution. Lives in its own partial file alongside StockMasterRepository.cs,
    // which needs the same one-word change already used for the return
    // repositories: `public class StockMasterRepository` → `public partial
    // class StockMasterRepository`.
    //
    // ASSUMPTIONS ON COLUMN NAMES — please verify against your actual schema:
    // purchase_item_details is assumed to have a `purchase_no` column (the
    // int PK used everywhere else, e.g. PurchaseRepository.GetItemsByPurchaseNo)
    // and sales_item_details is assumed to have `prefix` + `bill_no` columns
    // (the composite key used by SalesRepository.GetItems(prefix, billNo) —
    // confirmed by SalesItemDetail.Prefix/BillNo in SalesMaster.cs). If either
    // table actually names those columns differently, adjust the relevant
    // query inside AttachSourceDocuments below — everything else is unaffected.
    //
    // FIX (v3) — WHY THIS VERSION EXISTS:
    // v1 used a single UNION ALL across all five source tables with bare
    // NULL placeholders for the columns each branch didn't have. v2 tried to
    // fix the resulting Dapper "Error parsing column N" crash by wrapping
    // every column — including the NULLs — in an explicit CAST(... AS TYPE).
    // That still failed: in SQLite, NULL has no storage class of its own,
    // full stop — CAST(NULL AS INTEGER) is still reported back as plain NULL,
    // not as an INTEGER-typed NULL. So the column's underlying storage class
    // still varied row-to-row across the union (NULL on branches that don't
    // have that column, INTEGER/TEXT on the one branch that does), and
    // Dapper's per-column type inference — which locks in a CLR type from
    // whichever row it reads first — still broke the moment it hit a row
    // with the other storage class.
    //
    // v3 sidesteps the whole problem: instead of one UNION'd query with
    // heterogeneous columns, this runs FIVE separate, single-purpose queries
    // — one per source table — each selecting only its own real,
    // non-nullable columns. There is no mixed-type column anywhere, so this
    // class of error cannot occur. The five small result sets are merged
    // onto the report rows in C# afterward.
    // ═══════════════════════════════════════════════════════════════════════
    public partial class StockMasterRepository
    {
        /// <summary>Item/batch picker for the report's search combo — one row
        /// per real, existing batch (mirrors the return-entry batch lookups),
        /// with current stock included for display in the dropdown.</summary>
        /// <summary>
        /// Item/batch picker for the report — starts from item_master (LEFT
        /// JOIN into batches/ledger, same shape as
        /// StockMasterRepository.GetCurrentStockReport) rather than
        /// View_CurrentStock, so a brand-new item that has never been
        /// purchased/produced/sold — and therefore has no batch row at all —
        /// still produces one picker entry (BatchId = 0, CurrentQty = 0)
        /// instead of being silently absent from the dropdown. Non-Stockable
        /// items (category 3) are excluded, same policy as the Current Stock
        /// report. Every numeric column is CAST to REAL so Dapper never sees
        /// an Int64 where the model expects double (same root cause as the
        /// Current Stock report crash).
        /// </summary>
        public List<ItemBatchPickerLookup> GetBatchPickerLookups(string itemSearch = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string pattern = string.IsNullOrWhiteSpace(itemSearch) ? null : "%" + itemSearch.Trim() + "%";

                return db.Query<ItemBatchPickerLookup>(@"
                    SELECT
                        COALESCE(b.batch_id, 0)              AS BatchId,
                        m.item_code                          AS ItemCode,
                        m.item_name                          AS ItemName,
                        m.hsn_code                            AS HsnCode,
                        COALESCE(b.batch_number, '')          AS BatchNumber,
                        CAST(COALESCE(b.mrp, 0)           AS REAL) AS Mrp,
                        CAST(COALESCE(b.purchase_rate, 0) AS REAL) AS PurchaseRate,
                        CAST(COALESCE(b.sales_rate, 0)    AS REAL) AS SalesRate,
                        CAST(
                            COALESCE(SUM(t.qty_in), 0) - COALESCE(SUM(t.qty_out), 0)
                        AS REAL) AS CurrentQty
                    FROM item_master m
                    LEFT JOIN item_batch_details b         ON b.item_code = m.item_code
                    LEFT JOIN stock_transaction_details t  ON t.batch_id = b.batch_id
                    WHERE m.category <> 3   -- exclude Non-Stockable items entirely
                      AND (@Pattern IS NULL OR m.item_name LIKE @Pattern OR m.item_code LIKE @Pattern OR b.batch_number LIKE @Pattern)
                    GROUP BY m.item_code, b.batch_id
                    ORDER BY m.item_name, b.mrp",
                    new { Pattern = pattern }).ToList();
            }
        }

        /// <summary>
        /// Opening Stock's value — deliberately NOT date-bound, unlike
        /// GetStockValuation below. OpeningStockRepository.InsertLine stamps
        /// every OPENING-type ledger row's trans_date with DateTime.Now AT
        /// SAVE TIME, not the entry date the user picked on the form — so a
        /// trans_date &lt;= cutoff filter would silently exclude opening
        /// stock entered after the financial year it represents has already
        /// started, which is the normal case (nobody sets up the books on
        /// day one of the FY). Opening Stock instead just means "every
        /// OPENING-type row, whenever it was saved" — same WHERE clause
        /// OpeningStockRepository.GetOpeningStockReport already uses (its
        /// own grand-total footer is exactly this number), just collapsed
        /// to a single figure instead of one row per batch. Deliberately no
        /// category &lt;&gt; 3 exclusion either, for the same reason: match
        /// the Opening Stock Report's own total exactly, since that's the
        /// screen where this figure is actually entered/edited.
        ///
        /// NOT currently used by TaxLedgerRepository — Opening/Closing
        /// Stock accounting figures moved to manually-entered ac_head_master
        /// heads under reserved categories (see AccountCategoryRepository
        /// .OpeningStockCategoryCode/ClosingStockCategoryCode) instead of a
        /// computed inventory.db valuation. Left here in case a future
        /// screen wants "what does the physical stock actually value at"
        /// as a distinct, separate figure from what accounting has on
        /// record for it.
        /// </summary>
        public double GetOpeningStockValuation()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<double?>(@"
                    SELECT COALESCE(SUM((t.qty_in - t.qty_out) * b.purchase_rate), 0)
                    FROM stock_transaction_details t
                    JOIN item_batch_details b ON b.batch_id = t.batch_id
                    WHERE t.trans_type = 'OPENING'") ?? 0;
            }
        }

        /// <summary>
        /// Total stock valuation "as of" <paramref name="asOfDate"/> — same
        /// per-batch qty math as GetCurrentStockReport (only ledger rows
        /// with trans_date &lt;= asOfDate count, so this can reconstruct
        /// the value on any past date, not just today) and the same
        /// CurrentQty * PurchaseRate convention CurrentStockReportRow.Amount
        /// already uses, collapsed to a single grand total via a
        /// GROUP BY batch_id subquery. Same category &lt;&gt; 3 exclusion
        /// as every other stock report (Non-Stockable items never carry a
        /// stock value).
        ///
        /// NOT currently used by TaxLedgerRepository — see
        /// GetOpeningStockValuation's doc comment above for why.
        /// </summary>
        public double GetStockValuation(DateTime asOfDate)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string asOf = asOfDate.ToString("yyyy-MM-dd");

                return db.ExecuteScalar<double?>(@"
                    SELECT COALESCE(SUM(bal.qty * b.purchase_rate), 0)
                    FROM item_batch_details b
                    JOIN item_master m ON m.item_code = b.item_code
                    JOIN (
                        SELECT batch_id,
                               COALESCE(SUM(CASE WHEN trans_date <= @AsOf THEN qty_in  ELSE 0 END), 0)
                             - COALESCE(SUM(CASE WHEN trans_date <= @AsOf THEN qty_out ELSE 0 END), 0) AS qty
                        FROM stock_transaction_details
                        GROUP BY batch_id
                    ) bal ON bal.batch_id = b.batch_id
                    WHERE m.category <> 3",
                    new { AsOf = asOf }) ?? 0;
            }
        }

        // Raw per-row shape used by both history queries below — trans_date
        // comes back as TEXT (SQLite storage), parsed to DateTime afterward
        // same as the rest of this repository.
        private class LedgerRowWithBatch
        {
            public int TransId { get; set; }
            public string TransDate { get; set; }
            public string TransType { get; set; }
            public double QtyIn { get; set; }
            public double QtyOut { get; set; }
            public int BatchId { get; set; }
            public string BatchNumber { get; set; }
            public double Mrp { get; set; }
        }

        /// <summary>Full chronological ledger history for ONE batch, with a
        /// running balance and (where resolvable) a link back to the source
        /// document. Always computed over the batch's ENTIRE history — date
        /// range and transaction-type filtering is left to the caller
        /// (IndividualStockReportView) to apply on the returned list, so the
        /// RunningBalance value on every row stays correct regardless of
        /// what's currently being displayed.</summary>
        public List<StockTransactionReportRow> GetTransactionHistory(int batchId)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var ledger = db.Query<LedgerRowWithBatch>(@"
                    SELECT
                        t.trans_id      AS TransId,
                        t.trans_date    AS TransDate,
                        t.trans_type    AS TransType,
                        t.qty_in        AS QtyIn,
                        t.qty_out       AS QtyOut,
                        b.batch_id      AS BatchId,
                        b.batch_number  AS BatchNumber,
                        b.mrp           AS Mrp
                    FROM stock_transaction_details t
                    JOIN item_batch_details b ON b.batch_id = t.batch_id
                    WHERE t.batch_id = @BatchId
                    ORDER BY t.trans_date, t.trans_id",
                    new { BatchId = batchId }).ToList();

                return BuildRunningBalanceRows(db, ledger);
            }
        }

        /// <summary>Full chronological ledger history for EVERY batch of a
        /// given item, combined into one running balance — same shape and
        /// same "always correct regardless of the current filter" guarantee
        /// as GetTransactionHistory(batchId), just scoped to the whole item
        /// instead of a single MRP/batch. Each row's BatchNumber/Mrp still
        /// identifies which physical batch it came from, so a multi-MRP
        /// history doesn't read as ambiguous.</summary>
        public List<StockTransactionReportRow> GetTransactionHistoryForItem(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var ledger = db.Query<LedgerRowWithBatch>(@"
                    SELECT
                        t.trans_id      AS TransId,
                        t.trans_date    AS TransDate,
                        t.trans_type    AS TransType,
                        t.qty_in        AS QtyIn,
                        t.qty_out       AS QtyOut,
                        b.batch_id      AS BatchId,
                        b.batch_number  AS BatchNumber,
                        b.mrp           AS Mrp
                    FROM stock_transaction_details t
                    JOIN item_batch_details b ON b.batch_id = t.batch_id
                    WHERE b.item_code = @ItemCode
                    ORDER BY t.trans_date, t.trans_id",
                    new { ItemCode = itemCode }).ToList();

                return BuildRunningBalanceRows(db, ledger);
            }
        }

        /// <summary>Shared tail end of both history queries above: maps the
        /// raw ledger+batch rows onto StockTransactionReportRow, computes the
        /// cumulative RunningBalance in chronological order, and resolves
        /// each row's source document. Factored out so single-batch and
        /// combined-item mode can never drift apart on this logic.</summary>
        private List<StockTransactionReportRow> BuildRunningBalanceRows(IDbConnection db, List<LedgerRowWithBatch> ledger)
        {
            var rows = ledger.Select(t => new StockTransactionReportRow
            {
                TransId = t.TransId,
                TransDate = DateTime.Parse(t.TransDate),
                TransType = t.TransType,
                TransTypeLabel = TransactionModeOptions.LabelFor(t.TransType),
                QtyIn = t.QtyIn,
                QtyOut = t.QtyOut,
                BatchId = t.BatchId,
                BatchNumber = t.BatchNumber,
                Mrp = t.Mrp
            }).ToList();

            double running = 0;
            foreach (var r in rows)
            {
                running += r.NetQty;
                r.RunningBalance = running;
            }

            if (rows.Count > 0)
                AttachSourceDocuments(db, rows);

            return rows;
        }

        // ── Per-table lookup shapes used only by AttachSourceDocuments below.
        // Each maps exactly one query's real columns — no NULLs, no unions,
        // so there's nothing for Dapper's type inference to trip over. Kept
        // private/nested since nothing outside this file needs them.
        private class PurchaseSourceLink
        {
            public int TransId { get; set; }
            public int PurchaseNo { get; set; }
            // Vendor's actual bill number (purchase_summary_details.bill_no) —
            // display-only. Never used for navigation/lookup since bill
            // numbers aren't guaranteed unique across vendors; PurchaseNo
            // (the internal autonumber) remains the real key for that.
            public string BillNo { get; set; }
        }

        private class SalesSourceLink
        {
            public int TransId { get; set; }
            public string Prefix { get; set; }
            public int BillNo { get; set; }
            // Pre-built "PREFIX-N-suffix" string already stored on
            // sales_summary (see SalesSeriesRepository.BuildFormattedBillNo) —
            // reused here instead of re-concatenating Prefix+BillNo by hand,
            // which was silently dropping the suffix (FY/custom) portion.
            public string FormattedBillNo { get; set; }
        }

        private class SalesReturnSourceLink
        {
            public int TransId { get; set; }
            public int EntryNo { get; set; }
        }

        private class PurchaseReturnSourceLink
        {
            public int TransId { get; set; }
            public int EntryNo { get; set; }
        }

        private class OpeningStockSourceLink
        {
            public int TransId { get; set; }
            public string EntryNo { get; set; }
        }

        // ── Production ───────────────────────────────────────────────────
        // Two distinct trans_types can point back to the same production
        // entry: the PRODUCTION row (produced item, stored on the header —
        // production_summary_details.trans_id) and however many
        // PRODUCTION_CONSUME rows (raw materials consumed, one per
        // production_item_details row). Separate lookups since they come
        // from different tables/columns.
        private class ProductionSummarySourceLink
        {
            public int TransId { get; set; }
            public int ProductionNo { get; set; }
        }

        private class ProductionItemSourceLink
        {
            public int TransId { get; set; }
            public int ProductionNo { get; set; }
        }

        // ── Excess / Short Stock ────────────────────────────────────────
        // Two fully separate tables (excess_stock_item_details /
        // short_stock_item_details, each keyed by its own *_entry_no
        // column, not a shared one) — kept as two distinct lookups so this
        // stays correct however far the two tables diverge later.
        private class ExcessStockSourceLink
        {
            public int TransId { get; set; }
            public int EntryNo { get; set; }
        }

        private class ShortStockSourceLink
        {
            public int TransId { get; set; }
            public int EntryNo { get; set; }
        }

        /// <summary>Resolves each row's TransId against every document
        /// item-detail table via five separate, single-table queries (see the
        /// v3 fix note above the class for why this replaced the earlier
        /// UNION ALL approach), and copies the match (source type + display
        /// no + navigation key) onto the row. Rows with no match anywhere
        /// (e.g. a legacy/manually-inserted ledger entry) are simply left
        /// with SourceType = null — HasSource on the row model reflects this
        /// for the UI.</summary>
        private void AttachSourceDocuments(IDbConnection db, List<StockTransactionReportRow> rows)
        {
            var ids = rows.Select(r => r.TransId).ToList();
            var rowsById = rows.ToDictionary(r => r.TransId);

            // ── Purchase ─────────────────────────────────────────────────
            // Joined to purchase_summary_details for the vendor's actual bill
            // number (display only) — PurchaseNo (the internal autonumber)
            // is still what's used for edit-navigation, since bill_no can
            // legitimately repeat across different vendors/documents.
            var purchaseLinks = db.Query<PurchaseSourceLink>(@"
                SELECT
                    i.trans_id     AS TransId,
                    i.purchase_no  AS PurchaseNo,
                    s.bill_no      AS BillNo
                FROM purchase_item_details i
                JOIN purchase_summary_details s ON s.purchase_no = i.purchase_no
                WHERE i.trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in purchaseLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Purchase";
                row.PurchaseNo = link.PurchaseNo;
                // Fall back to the internal number only if the bill has no
                // vendor bill no recorded at all (shouldn't normally happen,
                // but avoids an empty-looking cell if it ever does).
                row.SourceDisplayNo = !string.IsNullOrWhiteSpace(link.BillNo)
                    ? link.BillNo
                    : $"Purchase #{link.PurchaseNo}";
            }

            // ── Sales ────────────────────────────────────────────────────
            // Joined to sales_summary for the pre-built formatted bill no
            // (includes any FY/custom suffix) instead of reconstructing
            // Prefix+BillNo by hand, which dropped the suffix.
            var salesLinks = db.Query<SalesSourceLink>(@"
                SELECT
                    i.trans_id           AS TransId,
                    i.prefix             AS Prefix,
                    i.bill_no            AS BillNo,
                    s.formatted_bill_no  AS FormattedBillNo
                FROM sales_item_details i
                JOIN sales_summary s ON s.prefix = i.prefix AND s.bill_no = i.bill_no
                WHERE i.trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in salesLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Sales";
                row.SalesPrefix = link.Prefix;
                row.SalesBillNo = link.BillNo;
                row.SourceDisplayNo = !string.IsNullOrWhiteSpace(link.FormattedBillNo)
                    ? link.FormattedBillNo
                    : $"{link.Prefix}{link.BillNo}";
            }

            // ── Sales Return ─────────────────────────────────────────────
            var salesReturnLinks = db.Query<SalesReturnSourceLink>(@"
                SELECT
                    trans_id AS TransId,
                    entry_no AS EntryNo
                FROM sales_return_item_details
                WHERE trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in salesReturnLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Sales Return";
                row.ReturnEntryNo = link.EntryNo;
                row.SourceDisplayNo = $"SR-{link.EntryNo}";
            }

            // ── Purchase Return ──────────────────────────────────────────
            var purchaseReturnLinks = db.Query<PurchaseReturnSourceLink>(@"
                SELECT
                    trans_id AS TransId,
                    entry_no AS EntryNo
                FROM purchase_return_item_details
                WHERE trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in purchaseReturnLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Purchase Return";
                row.ReturnEntryNo = link.EntryNo;
                row.SourceDisplayNo = $"PR-{link.EntryNo}";
            }

            // ── Opening Stock ────────────────────────────────────────────
            var openingStockLinks = db.Query<OpeningStockSourceLink>(@"
                SELECT
                    trans_id AS TransId,
                    entry_no AS EntryNo
                FROM opening_stock_details
                WHERE trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in openingStockLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Opening Stock";
                row.OpeningEntryNo = link.EntryNo;
                row.SourceDisplayNo = link.EntryNo;
            }

            // ── Production (produced item, posted on the header row) ───────
            var productionLinks = db.Query<ProductionSummarySourceLink>(@"
                SELECT
                    trans_id      AS TransId,
                    production_no AS ProductionNo
                FROM production_summary_details
                WHERE trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in productionLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Production";
                row.ProductionNo = link.ProductionNo;
                row.SourceDisplayNo = $"Production #{link.ProductionNo}";
            }

            // ── Production Consumption (raw material used, one per
            // production_item_details row) ─────────────────────────────────
            var productionConsumeLinks = db.Query<ProductionItemSourceLink>(@"
                SELECT
                    trans_id      AS TransId,
                    production_no AS ProductionNo
                FROM production_item_details
                WHERE trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in productionConsumeLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Production Consumption";
                row.ProductionNo = link.ProductionNo;
                row.SourceDisplayNo = $"Production #{link.ProductionNo}";
            }

            // ── Excess Stock ─────────────────────────────────────────────
            var excessStockLinks = db.Query<ExcessStockSourceLink>(@"
                SELECT
                    trans_id       AS TransId,
                    excess_entry_no AS EntryNo
                FROM excess_stock_item_details
                WHERE trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in excessStockLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Excess Stock";
                row.AdjustmentEntryNo = link.EntryNo;
                row.SourceDisplayNo = ExcessStockRepository.FormatEntryNo(link.EntryNo);
            }

            // ── Short Stock ──────────────────────────────────────────────
            var shortStockLinks = db.Query<ShortStockSourceLink>(@"
                SELECT
                    trans_id      AS TransId,
                    short_entry_no AS EntryNo
                FROM short_stock_item_details
                WHERE trans_id IN @Ids",
                new { Ids = ids });

            foreach (var link in shortStockLinks)
            {
                if (!rowsById.TryGetValue(link.TransId, out var row)) continue;
                row.SourceType = "Short Stock";
                row.AdjustmentEntryNo = link.EntryNo;
                row.SourceDisplayNo = ShortStockRepository.FormatEntryNo(link.EntryNo);
            }
        }
    }
}