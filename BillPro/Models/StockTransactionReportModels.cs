using System;
using System.Collections.Generic;
using System.Linq;

namespace BillPro.Models
{
    // ═══════════════════════════════════════════════════════════════════════
    // INDIVIDUAL STOCK REPORT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item/batch picker for the report's search combo — same shape
    /// as SalesReturnItemLookup/PurchaseReturnItemLookup, but generic (no
    /// "return" framing): one row per real batch, current stock included so
    /// the picker's grouped dropdown can show it alongside MRP, same UX as
    /// the return-entry item combos.</summary>
    public class ItemBatchPickerLookup
    {
        public int BatchId { get; set; }
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public string BatchNumber { get; set; }
        public double Mrp { get; set; }
        public double PurchaseRate { get; set; }
        public double SalesRate { get; set; }
        public double CurrentQty { get; set; }
    }

    /// <summary>One row per ledger movement (stock_transaction_details) for a
    /// single batch — OR, when "combine all batches" is on, for every batch
    /// of a single item — in chronological order, with a running balance
    /// and — where resolvable — a link back to the document that created it.
    /// RunningBalance reflects the true cumulative balance since the first
    /// movement in whatever set was loaded (one batch, or the whole item),
    /// independent of whatever date-range/type filter is currently narrowing
    /// the on-screen list — so a balance is always correct to read even
    /// mid-filter, same as a bank passbook.</summary>
    public class StockTransactionReportRow
    {
        public int TransId { get; set; }
        public DateTime TransDate { get; set; }
        public string TransType { get; set; }
        public string TransTypeLabel { get; set; }

        public double QtyIn { get; set; }
        public double QtyOut { get; set; }
        public double NetQty => QtyIn - QtyOut;
        public double RunningBalance { get; set; }

        // Which physical batch this movement belongs to — always populated
        // (single-batch mode: every row has the same value; combined mode:
        // this is what actually distinguishes one row from another beyond
        // just the source document). Needed so a combined, multi-MRP view
        // doesn't collapse into an ambiguous list.
        public int BatchId { get; set; }
        public string BatchNumber { get; set; }
        public double Mrp { get; set; }

        // Populated by StockMasterRepository.AttachSourceDocuments — null
        // SourceType means no matching row was found in any source table
        // (e.g. a manually-inserted/legacy ledger row with no document).
        public string SourceType { get; set; }
        public string SourceDisplayNo { get; set; }

        // Navigation key components — only the ones relevant to SourceType
        // are populated; see IndividualStockReportView.OpenSourceDocument.
        public int? PurchaseNo { get; set; }
        public string SalesPrefix { get; set; }
        public int? SalesBillNo { get; set; }
        public int? ReturnEntryNo { get; set; }
        public string OpeningEntryNo { get; set; }
        public int? ProductionNo { get; set; }

        /// <summary>EntryNo for Excess Stock / Short Stock adjustments —
        /// which module it belongs to is disambiguated by SourceType
        /// ("Excess Stock" vs "Short Stock"), same as ReturnEntryNo is
        /// shared between Sales Return / Purchase Return above.</summary>
        public int? AdjustmentEntryNo { get; set; }

        public bool HasSource => !string.IsNullOrEmpty(SourceType);
    }

    /// <summary>Raw shape returned by the UNION ALL source-lookup query in
    /// StockMasterRepository — one row per trans_id that was found in any of
    /// the document item-detail tables. Mapped onto StockTransactionReportRow
    /// afterwards.</summary>
    public class StockTransactionSourceLink
    {
        public int TransId { get; set; }
        public string SourceType { get; set; }
        public int? PurchaseNo { get; set; }
        public string SalesPrefix { get; set; }
        public int? SalesBillNo { get; set; }
        public int? ReturnEntryNo { get; set; }
        public string OpeningEntryNo { get; set; }
    }

    /// <summary>Fixed set of trans_type values currently written by the
    /// various entry repositories (StockMasterRepository.InsertLedger callers),
    /// with display labels for the transaction-mode filter and the grid's
    /// Type column. If a new module starts writing a new trans_type, add it
    /// here so it shows up in the filter instead of falling back to the raw
    /// code.</summary>
    public static class TransactionModeOptions
    {
        public static readonly List<(string Value, string Label)> All = new()
        {
            ("OPENING",      "Opening Stock"),
            ("PURCHASE",     "Purchase"),
            ("SALE",         "Sale"),
            ("SALE_RETURN",  "Sales Return"),
            ("PUR_RETURN",   "Purchase Return"),
            ("PRODUCTION",         "Production (Produced)"),
            ("PRODUCTION_CONSUME", "Production (Consumed)"),
            ("EXCESS_STOCK",       "Excess Stock"),
            ("SHORT_STOCK",        "Short Stock"),
        };

        public static string LabelFor(string transType)
        {
            foreach (var (value, label) in All)
                if (string.Equals(value, transType, StringComparison.OrdinalIgnoreCase))
                    return label;
            return transType; // unknown/future type — show the raw code rather than hide the row
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CURRENT STOCK REPORT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Master row for the Current Stock Report's item-wise /
    /// batch-wise master-detail view — one row per ItemCode, aggregating
    /// every batch already fetched into CurrentStockReportRow (still the
    /// batch-level model — MRP, Sales/Purchase Rate, CurrentQty, Amount stay
    /// per-batch there). Details holds those batch rows for Syncfusion's
    /// DetailsViewDefinition (RelationalColumn="Details"), same wiring
    /// SalesReturnItemReportView uses for its own nested grid.</summary>
    public class CurrentStockItemSummaryRow
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string CompanyName { get; set; }
        public string CategoryName { get; set; }

        // Collapsed-row totals — sum of every batch's CurrentQty/Amount for
        // this item, so a collapsed row still shows a meaningful total stock
        // figure rather than an arbitrary single batch's number.
        public double TotalCurrentQty { get; set; }
        public double TotalAmount { get; set; }

        public List<CurrentStockReportRow> Details { get; set; }

        public static List<CurrentStockItemSummaryRow> BuildFrom(IEnumerable<CurrentStockReportRow> rows)
        {
            return rows
                .GroupBy(r => r.ItemCode)
                .OrderBy(g => g.First().ItemName)
                .Select(g =>
                {
                    var lines = g.ToList();
                    return new CurrentStockItemSummaryRow
                    {
                        ItemCode = g.Key,
                        ItemName = lines[0].ItemName,
                        CompanyName = lines[0].CompanyName,
                        CategoryName = lines[0].CategoryName,
                        TotalCurrentQty = lines.Sum(r => r.CurrentQty),
                        TotalAmount = lines.Sum(r => r.Amount),
                        Details = lines
                    };
                })
                .ToList();
        }
    }
}