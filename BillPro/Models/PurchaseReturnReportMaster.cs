using System;
using System.Collections.Generic;
using System.Linq;

namespace BillPro.Models
{
    // ═══════════════════════════════════════════════════════════════════════
    // PURCHASE RETURN — ALL REPORT ROW MODELS
    // Mirror of SalesReturnReportMaster.cs, vendor-side. Every model backing
    // the three Purchase Return report queries (PurchaseReturnRepository.
    // reports.cs: GetReport / GetItemWiseReport / GetSettlementReport) lives
    // together here.
    // ═══════════════════════════════════════════════════════════════════════

    // ── FLAT REPORT ROW — backs PurchaseReturnReportView / GetReport ───────
    public class PurchaseReturnReportRow : BillPro.Helpers.IReportRow
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }
        public DateTime EntryDate { get; set; }
        public string OriginalBillNo { get; set; }
        public string BillType { get; set; }
        public string VendorName { get; set; }

        public double GrossAmount { get; set; }
        public double DiscountAmount { get; set; }
        public double TaxAmount { get; set; }
        public double NetTotal { get; set; }

        // Per-entry taxable amount split by tax rate, e.g. { "5" -> 4200.00,
        // "12" -> 1800.00 }. Keys are the tax percent formatted with
        // ToString("0.##") — populated by PurchaseReturnReportView from
        // PurchaseReturnRepository.GetTaxRateBreakup, one row's dictionary
        // always carrying every rate key that appears anywhere in the
        // loaded report (0 for rates this particular entry didn't use), so
        // the on-screen grid's per-rate columns can bind to a fixed key
        // ("TaxByRate[5]") without hitting a missing-key error.
        public Dictionary<string, double> TaxByRate { get; set; } = new();

        // Shown as a single "Others" column per the report spec — just
        // DiscountAmount here (unlike Sales, there's no separate round-off
        // column on purchase_return_summary to fold in).
        public double Others => DiscountAmount;

        public double ToCashAmount { get; set; }
        public double ToBankAmount { get; set; }
        public string BankHeadName { get; set; }
        public double ToCreditAmount { get; set; }

        public int IsCancelled { get; set; }

        public bool IsReportHighlighted => false;
        public bool IsReportFieldBlank(string propertyName) => false;
        public int GetMergedLabelSpan(out string mergedLabel) { mergedLabel = null; return 0; }
    }

    // ── RAW TAX-RATE BREAKUP ROW — backs GetTaxRateBreakup ─────────────────
    // One row per (entry, tax rate) grouping. PurchaseReturnReportView
    // pivots a list of these into each PurchaseReturnReportRow.TaxByRate
    // before building the dynamic columns.
    public class PurchaseReturnTaxRateBreakupRow
    {
        public int EntryNo { get; set; }
        public double TaxPercent { get; set; }
        public double TaxableAmount { get; set; }
    }

    // ── ITEM-WISE REPORT ROWS — backs GetItemWiseReport ─────────────────────
    // Mirror of SalesReturnItemReportRow/SalesReturnItemSummaryRow, vendor-side.
    public class PurchaseReturnItemReportRow
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }
        public DateTime EntryDate { get; set; }
        public string VendorName { get; set; }

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

        public bool IsSummaryRow { get; set; }
    }

    public class PurchaseReturnItemSummaryRow
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }

        public double TotalQuantity { get; set; }
        public double TotalDiscount { get; set; }
        public double TotalTaxAmount { get; set; }
        public double TotalPrice { get; set; }

        public List<PurchaseReturnItemReportRow> Details { get; set; } = new();

        public static List<PurchaseReturnItemSummaryRow> BuildFrom(IEnumerable<PurchaseReturnItemReportRow> rows)
        {
            return rows
                .GroupBy(r => r.ItemCode)
                .Select(g => new PurchaseReturnItemSummaryRow
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
    // Mirror of SalesReturnSettlementReportRow, vendor-side.
    public class PurchaseReturnSettlementReportRow
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }
        public DateTime EntryDate { get; set; }
        public string VendorName { get; set; }

        public double NetTotal { get; set; }
        public double ToCash { get; set; }
        public double ToBank { get; set; }
        public string BankHeadName { get; set; }
        public double ToCredit { get; set; }
    }
}