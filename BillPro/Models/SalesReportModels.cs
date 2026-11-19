using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using BillPro.Helpers;

namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // SALES REPORT  — one row per bill (mirrors PurchaseReportRow)
    // ─────────────────────────────────────────────────────────────────────
    public class SalesReportRow
    {
        public string Prefix { get; set; }
        public int BillNo { get; set; }
        public string FormattedBillNo { get; set; }
        public DateTime BillDate { get; set; }
        public string BillType { get; set; }
        public string CustomerName { get; set; }
        public string CustomerGstNo { get; set; }

        public double GrossAmount { get; set; }
        public double Discount { get; set; }
        public double RoundOff { get; set; }
        public double Tax { get; set; }
        public double Total { get; set; }

        // Per-bill taxable amount split by GST rate, e.g. { "5" -> 4200.00,
        // "12" -> 1800.00 }. Keys are the tax percent formatted with
        // ToString("0.##") (so "5", "12", "2.5" — no trailing zeros), and are
        // populated by SalesReportView from SalesRepository.GetTaxRateBreakup,
        // one row's dictionary always carrying every rate key that appears
        // anywhere in the loaded report (0 for rates this particular bill
        // didn't use), so the on-screen grid's per-rate columns can bind to a
        // fixed key ("TaxByRate[5]") without hitting a missing-key error on
        // rows that happen not to have that rate.
        public Dictionary<string, double> TaxByRate { get; set; } = new();

        // Discount + round-off, shown as a single "Others" column per the
        // report spec — neither is a tax-rate-specific amount, so they don't
        // belong split by rate, but they still need to reconcile
        // Taxable(by rate) + Tax + Others = Total.
        public double Others => Discount + RoundOff;

        public static SalesReportRow FromSummary(SalesSummary s) => new SalesReportRow
        {
            Prefix = s.Prefix,
            BillNo = s.BillNo,
            FormattedBillNo = s.FormattedBillNo,
            BillDate = s.BillDate,
            BillType = s.BillType,
            CustomerName = string.IsNullOrWhiteSpace(s.CustomerName) ? "Walk-in" : s.CustomerName,
            CustomerGstNo = s.CustomerGstNo,
            GrossAmount = s.GrossAmount,
            Discount = s.DiscountAmount,
            RoundOff = s.RoundOff,
            Tax = s.TaxAmount,
            Total = s.NetTotal
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Raw result row for SalesRepository.GetTaxRateBreakup — one row per
    // (bill, tax rate) grouping. SalesReportView pivots a list of these into
    // each SalesReportRow.TaxByRate before building the dynamic columns.
    // ─────────────────────────────────────────────────────────────────────
    public class SalesTaxRateBreakupRow
    {
        public string Prefix { get; set; }
        public int BillNo { get; set; }
        public double TaxPercent { get; set; }
        public double TaxableAmount { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ITEM-WISE SALES REPORT — one row per sold line, joined to its bill
    // header (mirrors PurchaseItemReportRow, including the collapsed/
    // expanded TOTAL-row export convention).
    // ─────────────────────────────────────────────────────────────────────
    public class SalesItemReportRow : IReportRow
    {
        public string Prefix { get; set; }
        public int BillNo { get; set; }
        public string FormattedBillNo { get; set; }
        public DateTime BillDate { get; set; }
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

        // Set by SalesItemwiseReportView.BuildExportRows() for two cases:
        // the "— TOTAL —" line under an expanded item's detail lines, and
        // the single rolled-up line standing in for a collapsed item (no
        // detail lines exported for it). Both are aggregates, not a real
        // sales line, so Print/PDF/Excel bold them and blank out the
        // per-bill-only fields below via IReportRow — mirrors
        // PurchaseItemReportRow exactly.
        public bool IsSummaryRow { get; set; }

        private static readonly HashSet<string> BlankOnSummary = new()
        {
            nameof(BillDate), nameof(CustomerName), nameof(HsnCode),
            nameof(BatchNumber), nameof(Rate), nameof(TaxRate)
        };

        bool IReportRow.IsReportHighlighted => IsSummaryRow;

        bool IReportRow.IsReportFieldBlank(string propertyName) =>
            IsSummaryRow && BlankOnSummary.Contains(propertyName);

        // Summary rows ("— TOTAL —" / rolled-up item lines) span the first
        // 3 report columns (Bill Date, Bill No, Customer — see
        // SalesItemwiseReportView.BuildReportColumns) as one centered cell
        // holding FormattedBillNo (which BuildExportRows sets to
        // TotalRowLabel), instead of showing that label cramped into just
        // the Bill No column while Date/Customer sit blank beside it.
        int IReportRow.GetMergedLabelSpan(out string mergedLabel)
        {
            mergedLabel = FormattedBillNo;
            return IsSummaryRow ? 3 : 0;
        }
    }

    // Outer, one-row-per-item summary used by the Master-Details grid.
    public class SalesItemSummaryRow
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }

        public double TotalQuantity { get; set; }
        public double TotalDiscount { get; set; }
        public double TotalTaxAmount { get; set; }
        public double TotalPrice { get; set; }

        // Nested collection the DetailsViewDefinition's RelationalColumn="Details" binds to.
        public ObservableCollection<SalesItemReportRow> Details { get; set; } = new();

        public static IEnumerable<SalesItemSummaryRow> BuildFrom(IEnumerable<SalesItemReportRow> rows)
        {
            return rows
                .GroupBy(r => r.ItemCode)
                .OrderBy(g => g.First().ItemName)
                .Select(g =>
                {
                    var lines = g.OrderByDescending(r => r.BillDate).ToList();
                    return new SalesItemSummaryRow
                    {
                        ItemCode = g.Key,
                        ItemName = lines[0].ItemName,
                        HsnCode = lines[0].HsnCode,
                        TotalQuantity = lines.Sum(r => r.Quantity),
                        TotalDiscount = lines.Sum(r => r.Discount),
                        TotalTaxAmount = lines.Sum(r => r.TaxAmount),
                        TotalPrice = lines.Sum(r => r.Total),
                        Details = new ObservableCollection<SalesItemReportRow>(lines)
                    };
                });
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // CASH / BANK / CREDIT SETTLEMENT REPORT — one row per bill (mirrors
    // PurchaseSettlementReportRow).
    // ─────────────────────────────────────────────────────────────────────
    public class SalesSettlementReportRow
    {
        public string Prefix { get; set; }
        public int BillNo { get; set; }
        public string FormattedBillNo { get; set; }
        public DateTime BillDate { get; set; }
        public string CustomerName { get; set; }

        public double NetTotal { get; set; }
        public double ToCash { get; set; }
        public double ToBank { get; set; }
        public string BankHeadName { get; set; }
        public double ToCredit { get; set; }
    }
}