using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BillPro.Helpers;

namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // PURCHASE REPORT ROW — one row per bill. Flat/plain (not
    // ObservableObject — report rows are load-once, display-only, same as
    // CurrentStockReportRow) built from PurchaseSummary by PurchaseReportView.
    // "Others" is Cash Discount + Rounded Amount, combined into a single
    // column per the report spec.
    // ─────────────────────────────────────────────────────────────────────
    public class PurchaseReportRow
    {
        // Needed so right-click "Edit This Bill" / "View Details" on the
        // report grid knows which bill to reload — not shown as a column.
        public int PurchaseNo { get; set; }
        public DateTime PurchaseDate { get; set; }
        public string BillNo { get; set; }
        public DateTime BillDate { get; set; }
        public string VendorName { get; set; }
        public string GstNo { get; set; }
        public decimal Amount { get; set; }
        public decimal Tax { get; set; }
        public decimal Others { get; set; }
        public decimal Total { get; set; }

        // Per-bill taxable amount split by tax rate, e.g. { "5" -> 4200.00,
        // "12" -> 1800.00 }. Keys are the tax rate formatted with
        // ToString("0.##") — populated by PurchaseReportView from
        // PurchaseRepository.GetTaxRateBreakup, one row's dictionary always
        // carrying every rate key that appears anywhere in the loaded
        // report (0 for rates this particular bill didn't use), so the
        // on-screen grid's per-rate columns can bind to a fixed key
        // ("TaxByRate[5]") without hitting a missing-key error.
        public Dictionary<string, double> TaxByRate { get; set; } = new();

        public static PurchaseReportRow FromSummary(PurchaseSummary s) => new PurchaseReportRow
        {
            PurchaseNo = s.PurchaseNo,
            PurchaseDate = s.PurchaseDate,
            BillNo = s.BillNo,
            BillDate = s.BillDate,
            VendorName = s.VendorName,
            GstNo = s.GstNo,
            Amount = s.PriceAmount,
            Tax = s.TaxTotal,
            Others = s.CashDiscount + s.RoundedAmount,
            Total = s.NetTotal
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Raw result row for PurchaseRepository.GetTaxRateBreakup — one row per
    // (bill, tax rate) grouping. PurchaseReportView pivots a list of these
    // into each PurchaseReportRow.TaxByRate before building the dynamic
    // columns.
    // ─────────────────────────────────────────────────────────────────────
    public class PurchaseTaxRateBreakupRow
    {
        public int PurchaseNo { get; set; }
        public double TaxRate { get; set; }
        public double TaxableAmount { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ITEM-WISE PURCHASE REPORT ROW — one row per purchased item line,
    // joined back to its bill header for display context.
    // ─────────────────────────────────────────────────────────────────────
    public class PurchaseItemReportRow : IReportRow
    {
        public int PurchaseNo { get; set; }
        public DateTime PurchaseDate { get; set; }
        public string BillNo { get; set; }
        public DateTime BillDate { get; set; }
        public string VendorName { get; set; }
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public decimal Mrp { get; set; }
        public decimal Rate { get; set; }
        public decimal Quantity { get; set; }
        public decimal Discount { get; set; }
        public decimal TaxRate { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal Price { get; set; }

        // Set by PurchaseItemReportView.BuildExportRows() for two cases:
        // the "— TOTAL —" line under an expanded item's detail lines, and
        // the single rolled-up line standing in for a collapsed item (no
        // detail lines exported for it). Both are aggregates, not a real
        // purchase line, so Print/PDF/Excel bold them and blank out the
        // per-bill-only fields below via IReportRow.
        public bool IsSummaryRow { get; set; }

        private static readonly HashSet<string> BlankOnSummary = new()
        {
            nameof(PurchaseDate), nameof(BillDate), nameof(VendorName),
            nameof(HsnCode), nameof(Mrp), nameof(Rate), nameof(TaxRate)
        };

        bool IReportRow.IsReportHighlighted => IsSummaryRow;

        bool IReportRow.IsReportFieldBlank(string propertyName) =>
            IsSummaryRow && BlankOnSummary.Contains(propertyName);

        // Summary rows ("— TOTAL —" / rolled-up item lines) span the first
        // 3 report columns (Pur Date, Bill No, Supplier — see
        // BuildReportColumns) as one centered cell holding BillNo (which
        // BuildExportRows sets to TotalRowLabel), instead of showing that
        // label cramped into just the Bill No column while Date/Supplier
        // sit blank beside it.
        int IReportRow.GetMergedLabelSpan(out string mergedLabel)
        {
            mergedLabel = BillNo;
            return IsSummaryRow ? 3 : 0;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ITEM-WISE PURCHASE SUMMARY ROW — one row per item, aggregated across
    // every PurchaseItemReportRow for that item in the selected date range.
    // Only fields that are genuinely additive/common are summarized here
    // (Qty, Discount, Tax, Price) — Rate/MRP are deliberately excluded
    // since they can differ bill to bill; those stay visible only in the
    // expanded Details rows. Feeds the Master-Details grid in
    // PurchaseItemReportView: the outer grid is bound to a
    // List<PurchaseItemSummaryRow>, and each row's Details collection is
    // bound as the nested grid's ItemsSource via
    // GridViewDefinition.RelationalColumn.
    // ─────────────────────────────────────────────────────────────────────
    public class PurchaseItemSummaryRow
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public decimal TotalQuantity { get; set; }
        public decimal TotalDiscount { get; set; }
        public decimal TotalTaxAmount { get; set; }
        public decimal TotalPrice { get; set; }

        public ObservableCollection<PurchaseItemReportRow> Details { get; set; }
            = new ObservableCollection<PurchaseItemReportRow>();

        // Groups by ItemCode (the real identity). ItemName/HsnCode are
        // taken from the first line in the group — they're expected to be
        // consistent per item, but if they ever drift, the group still
        // holds together correctly since ItemCode alone drives the split.
        public static List<PurchaseItemSummaryRow> BuildFrom(IEnumerable<PurchaseItemReportRow> rows)
        {
            return rows
                .GroupBy(r => r.ItemCode)
                .Select(g =>
                {
                    var first = g.First();
                    return new PurchaseItemSummaryRow
                    {
                        ItemCode = first.ItemCode,
                        ItemName = first.ItemName,
                        HsnCode = first.HsnCode,
                        TotalQuantity = g.Sum(x => x.Quantity),
                        TotalDiscount = g.Sum(x => x.Discount),
                        TotalTaxAmount = g.Sum(x => x.TaxAmount),
                        TotalPrice = g.Sum(x => x.Price),
                        Details = new ObservableCollection<PurchaseItemReportRow>(
                            g.OrderByDescending(x => x.PurchaseDate))
                    };
                })
                .OrderBy(s => s.ItemName)
                .ToList();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // CASH / BANK / CREDIT SETTLEMENT REPORT ROW — one row per bill,
    // showing how the bill's net total was actually settled.
    // ─────────────────────────────────────────────────────────────────────
    public class PurchaseSettlementReportRow
    {
        public int PurchaseNo { get; set; }
        public DateTime PurchaseDate { get; set; }
        public string BillNo { get; set; }
        public string VendorName { get; set; }
        public decimal NetTotal { get; set; }
        public decimal ToCash { get; set; }
        public decimal ToBank { get; set; }
        public string BankHeadName { get; set; }
        public decimal ToCredit { get; set; }
    }
}