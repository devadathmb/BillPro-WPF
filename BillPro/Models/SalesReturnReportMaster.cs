using System;
using System.Collections.Generic;
using System.Linq;

namespace BillPro.Models
{
    // ═══════════════════════════════════════════════════════════════════════
    // SALES RETURN — ALL REPORT ROW MODELS
    // Every model backing the three Sales Return report queries
    // (SalesReturnRepository.reports.cs: GetReport / GetItemWiseReport /
    // GetSettlementReport) lives together here.
    // ═══════════════════════════════════════════════════════════════════════

    // ── FLAT REPORT ROW — backs SalesReturnReportView / GetReport ──────────
    // One row per return entry. Combines both the "list" and "settlement"
    // concerns into one flat row (unlike Sales, a return doesn't need a
    // separate item-wise/settlement split screen for its main report — the
    // whole document is small enough to show in one grid).
    public class SalesReturnReportRow : BillPro.Helpers.IReportRow
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }
        public DateTime EntryDate { get; set; }
        public string OriginalBillNo { get; set; }
        public string BillType { get; set; }
        public string CustomerName { get; set; }

        public double GrossAmount { get; set; }
        public double DiscountAmount { get; set; }
        public double TaxAmount { get; set; }
        public double NetTotal { get; set; }

        // Per-entry taxable amount split by tax rate, e.g. { "5" -> 4200.00,
        // "12" -> 1800.00 }. Keys are the tax percent formatted with
        // ToString("0.##") — populated by SalesReturnReportView from
        // SalesReturnRepository.GetTaxRateBreakup, one row's dictionary
        // always carrying every rate key that appears anywhere in the
        // loaded report (0 for rates this particular entry didn't use), so
        // the on-screen grid's per-rate columns can bind to a fixed key
        // ("TaxByRate[5]") without hitting a missing-key error.
        public Dictionary<string, double> TaxByRate { get; set; } = new();

        // Shown as a single "Others" column per the report spec — just
        // DiscountAmount here (unlike Sales, there's no separate round-off
        // column on sales_return_summary to fold in).
        public double Others => DiscountAmount;

        public double ToCashAmount { get; set; }
        public double ToBankAmount { get; set; }
        public string BankHeadName { get; set; }
        public double ToCreditAmount { get; set; }

        public int IsCancelled { get; set; }

        // IReportRow — this row type doesn't need highlighting/blanking/merging.
        public bool IsReportHighlighted => false;
        public bool IsReportFieldBlank(string propertyName) => false;
        public int GetMergedLabelSpan(out string mergedLabel) { mergedLabel = null; return 0; }
    }

    // ── RAW TAX-RATE BREAKUP ROW — backs GetTaxRateBreakup ─────────────────
    // One row per (entry, tax rate) grouping. SalesReturnReportView pivots a
    // list of these into each SalesReturnReportRow.TaxByRate before
    // building the dynamic columns.
    public class SalesReturnTaxRateBreakupRow
    {
        public int EntryNo { get; set; }
        public double TaxPercent { get; set; }
        public double TaxableAmount { get; set; }
    }

    // ── ITEM-WISE REPORT ROWS — backs GetItemWiseReport ─────────────────────
    // Mirrors SalesItemReportRow/SalesItemSummaryRow, adapted for returns:
    // no series/prefix, so "FormattedBillNo" becomes "FormattedEntryNo" and
    // there's no separate BillNo/Prefix pair — EntryNo alone is the key used
    // for edit-navigation (EntryEditNavigator with a plain int, same as
    // PurchaseEntry's channel).
    public class SalesReturnItemReportRow
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }
        public DateTime EntryDate { get; set; }
        public string CustomerName { get; set; }

        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public string BatchNumber { get; set; }

        public double Rate { get; set; }
        public double Quantity { get; set; }
        public double Discount { get; set; }
        public double TaxRate { get; set; }
        public double TaxAmount { get; set; }
        public double Total { get; set; }

        // Used only by the export/print "— TOTAL —" rolled-up row — same
        // convention as SalesItemReportRow.IsSummaryRow.
        public bool IsSummaryRow { get; set; }
    }

    // One row per item — the outer Master-Details grid row. Carries its own
    // Details collection of SalesReturnItemReportRow for the nested grid.
    public class SalesReturnItemSummaryRow
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }

        public double TotalQuantity { get; set; }
        public double TotalDiscount { get; set; }
        public double TotalTaxAmount { get; set; }
        public double TotalPrice { get; set; }

        public List<SalesReturnItemReportRow> Details { get; set; } = new();

        public static List<SalesReturnItemSummaryRow> BuildFrom(IEnumerable<SalesReturnItemReportRow> rows)
        {
            return rows
                .GroupBy(r => r.ItemCode)
                .Select(g => new SalesReturnItemSummaryRow
                {
                    ItemCode = g.Key,
                    ItemName = g.First().ItemName,
                    HsnCode = g.First().HsnCode,
                    TotalQuantity = g.Sum(r => r.Quantity),
                    TotalDiscount = g.Sum(r => r.Discount),
                    TotalTaxAmount = g.Sum(r => r.TaxAmount),
                    TotalPrice = g.Sum(r => r.Total),
                    Details = g.OrderByDescending(r => r.EntryDate).ToList()
                })
                .OrderBy(s => s.ItemName)
                .ToList();
        }
    }

    // ── SETTLEMENT REPORT ROW — backs GetSettlementReport ───────────────────
    // Mirrors SalesSettlementReportRow, keyed on EntryNo instead of
    // Prefix+BillNo.
    public class SalesReturnSettlementReportRow
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }
        public DateTime EntryDate { get; set; }
        public string CustomerName { get; set; }

        public double NetTotal { get; set; }
        public double ToCash { get; set; }
        public double ToBank { get; set; }
        public string BankHeadName { get; set; }
        public double ToCredit { get; set; }
    }
}