using System;
using System.Windows;

namespace BillPro.Helpers
{
    // Optional contract a report row type can implement to influence how
    // Print/PDF/Excel render it, on top of the normal per-column reflection
    // path every row goes through. Rows that don't implement this render
    // exactly as before — nothing changes unless a row opts in.
    public interface IReportRow
    {
        // True for a row that summarizes/aggregates others — a totals line
        // for an expanded item, or a collapsed item's single rolled-up
        // line. Renderers bold/shade these to set them apart from ordinary
        // detail rows.
        bool IsReportHighlighted { get; }

        // Column values that don't genuinely apply to this row (e.g.
        // Rate/MRP on a totals line summed across differing rates) should
        // render blank instead of a misleading default/zero value.
        // propertyName is the row-type property backing that column
        // (ReportColumn.PropertyName), so implementers can check it by
        // nameof(...) against their own properties.
        bool IsReportFieldBlank(string propertyName);

        // For a row that wants a single label drawn once, centered, across
        // the leading columns of the report — instead of the normal
        // per-column values in those slots (e.g. a "— TOTAL —" row whose
        // label should span Date/Bill/Supplier as one merged, centered
        // cell rather than sitting cramped in just the first column).
        // Return 0 (or 1) for an ordinary row that renders every column
        // normally. Return N > 1 to merge the first N columns (in the
        // report's column order) into one cell containing mergedLabel,
        // centered; columns from index N onward still render normally.
        int GetMergedLabelSpan(out string mergedLabel);
    }

    // Second, separately-optional contract layered on top of IReportRow for
    // row types that also want date-boundary/opening-balance style emphasis
    // (DaybookLedgerRow, LedgerTransactionReportRow). Kept apart from
    // IReportRow itself so existing IReportRow implementers elsewhere in the
    // app (SalesItemReportRow, PurchaseItemReportRow, CurrentStockReportRow,
    // etc.) don't have to implement members they don't use — adding a
    // member directly to IReportRow breaks every existing implementer.
    public interface IReportRowEmphasis
    {
        // Grouping key for the row (e.g. its date, "yyyy-MM-dd") — purely a
        // print/PDF/Excel emphasis hint, unrelated to any query/sort
        // grouping. Whenever this differs from the previous row's
        // (non-null) key, THIS row is drawn slightly bolder/bigger than
        // normal, marking where a new group (typically a new date) begins
        // in what is otherwise a flat, ungrouped table. Return null to
        // opt this row out of the comparison entirely — it neither
        // triggers emphasis on itself nor updates what the "previous key"
        // was for the row after it. Rows that already get their own
        // full IsReportHighlighted treatment (e.g. a Total row), or that
        // deliberately want a size-free callout (e.g. an Opening Balance
        // row), should return null here.
        string ReportGroupKey { get; }

        // Optional background tint for this row in PDF/Excel (colored
        // exports only — the black & white Print output ignores this,
        // same as it already ignores the colored header background).
        // Null = no special background; the normal alternating stripe (if
        // any) still applies. Non-null replaces the alternating stripe for
        // this one row — e.g. a soft amber tint for an Opening Balance row
        // (color only, no size change — see ReportGroupKey above), or an
        // extra tint on a Total row on top of its existing bold/larger text.
        System.Windows.Media.Color? ReportRowBackground { get; }

        // Extra points added on top of whatever size bump this row already
        // gets from IsReportHighlighted/group-start emphasis — e.g. the
        // Day Book's date-header row wants to read a little larger than an
        // ordinary highlighted row (Day Total). 0 = no extra bump.
        double ReportExtraFontBump { get; }

        // True draws a solid line above AND below this row's full width,
        // like a boxed section heading — e.g. the Day Book's date-header
        // row, to visually separate one day's block from the next.
        bool ReportRowBottomBorder { get; }
    }

    // Describes one exported column: header text, source property name (read
    // via reflection), optional fixed width, optional .NET format string
    // ("N2", "N3", etc.), and text alignment.
    //
    // Used by ReportExcelHelper. (Printing now goes through the grid itself
    // via ReportPrintHelper.Print(SfDataGrid, ...), so it no longer needs
    // this class — but Excel export isn't grid-native, so it still does.)
    public class ReportColumn
    {
        public string Header { get; }
        public string PropertyName { get; }
        public double? Width { get; }
        public string Format { get; }
        public TextAlignment Alignment { get; }

        // Optional escape hatch for columns that don't correspond to a real,
        // reflectable property on the row type — e.g. a per-tax-rate
        // taxable-amount column backed by a Dictionary<string,double> whose
        // keys vary report-to-report (GST rate columns generated on the fly
        // from whatever rates actually appear in the loaded rows). When set,
        // every renderer (Print/PDF/Excel) calls this instead of doing
        // PropertyInfo reflection against PropertyName. PropertyName is
        // still required in this case — it's used as the key for
        // IReportRow.IsReportFieldBlank(propertyName) and should just be a
        // unique, stable label (e.g. "TaxRate_5").
        public Func<object, object> ValueSelector { get; }

        public ReportColumn(string header, string propertyName, double? width = null,
            string format = null, TextAlignment alignment = TextAlignment.Left,
            Func<object, object> valueSelector = null)
        {
            Header = header;
            PropertyName = propertyName;
            Width = width;
            Format = format;
            Alignment = alignment;
            ValueSelector = valueSelector;
        }
    }

    // Optional second header tier, layered ABOVE the normal ReportColumn
    // header row — e.g. "EXPENSES" spanning the Particulars/Amount pair of
    // columns on the left, "INCOME" spanning the pair on the right, matching
    // the big section headings already shown above each SfDataGrid in forms
    // like TradingProfitLossReportView. Purely additive: a report that
    // doesn't pass any column groups renders exactly as it did before (no
    // extra header row, no divider). Every boundary between two consecutive
    // groups also gets a vertical divider line drawn down through the
    // header and every data row — the exported equivalent of the black
    // Border already separating the two SfDataGrids on screen.
    //
    // The ColumnCounts across all groups must add up to exactly the number
    // of columns in the accompanying ReportColumn list, in the same order
    // (group 1 covers columns [0..ColumnCount1), group 2 covers the next
    // ColumnCount2 columns, and so on).
    public class ReportColumnGroup
    {
        public string Header { get; }
        public int ColumnCount { get; }

        public ReportColumnGroup(string header, int columnCount)
        {
            Header = header;
            ColumnCount = columnCount;
        }
    }
}