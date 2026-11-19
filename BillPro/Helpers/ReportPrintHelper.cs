using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BillPro.Helpers
{
    public static class ReportPrintHelper
    {
        public static void Print(
            string title,
            IList<ReportColumn> columns,
            IEnumerable rows,
            IEnumerable<string> footerLines = null,
            string subTitle = null,
            bool landscape = true,
            Thickness? pageMargin = null,
            double baseFontSize = 9.0,
            IList<ReportColumnGroup> columnGroups = null)
        {
            if (columns == null) throw new ArgumentNullException(nameof(columns));

            // Printer output is always black & white — color is reserved for
            // the PDF export (see ReportPdfHelper), which also adds the
            // alternating row shading. Keeps ink usage predictable on a
            // physical printer regardless of what printer is selected.
            const bool useColor = false;

            var printDialog = new PrintDialog();
            if (printDialog.ShowDialog() != true)
                return;

            var rowList = rows?.Cast<object>().ToList() ?? new List<object>();
            var footerList = footerLines?.ToList() ?? new List<string>();
            Thickness margin = pageMargin ?? new Thickness(28);

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var paginator = new ReportDocumentPaginator(
                    title, subTitle, columns, rowList, footerList, useColor,
                    printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight, margin, baseFontSize,
                    columnGroups);

                printDialog.PrintDocument(paginator, title);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private sealed class ReportDocumentPaginator : DocumentPaginator
        {
            private readonly double _rowHeight;
            private readonly double _columnHeaderHeight;
            private readonly double _groupHeaderHeight;
            private readonly double _footerLineHeight;
            private readonly double _titleHeight;
            private readonly double _subTitleHeight;
            private readonly double _printedLineHeight;

            // Company Name / Address heading block, drawn centered above
            // everything else (title included) on the first page only —
            // see ReportCompanyHeader for where the text comes from.
            private readonly string _companyName;
            private readonly string _companyAddress;
            private readonly double _companyNameHeight;
            private readonly double _companyAddressHeight;
            private readonly double _companyNameFontSize;
            private readonly double _companyAddressFontSize;

            private readonly double _baseFontSize;
            private readonly double _titleFontSize;
            private readonly double _headerFontSize;
            private readonly double _footerFontSize;

            private static readonly SolidColorBrush ColorHeaderBg = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));

            // Light enough to stay cheap on ink and still read clearly on a
            // B&W printer — same idea as the PDF/Excel alternating stripe,
            // just grey instead of a blue tint.
            private static readonly SolidColorBrush AltRowBg = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            private static readonly Typeface Regular = new Typeface("Segoe UI");
            private static readonly Typeface Bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            private readonly string _title;
            private readonly string _subTitle;
            private readonly IList<ReportColumn> _columns;
            private readonly List<object> _rows;
            private readonly List<string> _footerLines;
            private readonly bool _useColor;
            private readonly Size _pageSize;
            private readonly Thickness _margin;
            private readonly double[] _colX;
            private readonly double[] _colW;
            private readonly List<PropertyInfo> _props;

            // Column-group support (see ReportColumnGroup) — null/empty
            // when the caller didn't pass any groups, in which case
            // nothing about the existing rendering changes at all.
            private readonly IList<ReportColumnGroup> _columnGroups;
            // Column index (0-based) each group starts at, e.g. groups of
            // sizes [2,2] over 4 columns -> [0, 2]. Used both to draw the
            // merged group-header cells and to know where the vertical
            // divider lines between groups belong (everywhere except the
            // very first boundary at index 0).
            private readonly List<int> _groupStartCol;

            // Extra breathing room on either side of a group divider so
            // cell text doesn't sit flush against the line — plain 3px
            // column padding (see the "- 6" in DrawText calls below) reads
            // fine between ordinary columns, but crowds visibly once
            // there's a bold divider line to sit next to. Zero for every
            // column except the one right before / right after a divider.
            private readonly double[] _colExtraLeftPad;
            private readonly double[] _colExtraRightPad;
            private const double DividerExtraPad = 10;

            private readonly List<int> _pageStartRow = new();
            private bool _paginated;
            private int _pageCount = 1;

            // Precomputed once, up front (not per-page), since "group start"
            // depends on the PREVIOUS row in the full, unpaginated sequence —
            // a row that happens to fall on a page boundary must still know
            // whether it started a new group on the page before it.
            // Rows whose ReportGroupKey is null (e.g. a Total row, or a row
            // that deliberately opts out) neither set nor clear this — they
            // just don't take part in the comparison at all, in either
            // direction.
            private readonly bool[] _groupStart;

            // Footer summary lines ("Label: value") split into columns so every
            // line's colon lines up regardless of label/value length.
            private readonly List<(string Label, string Value)> _footerParsed;
            private readonly double _footerLabelColWidth;
            private readonly double _footerColonWidth;
            private readonly double _footerValueColWidth;
            private const double FooterColumnGap = 6;

            public ReportDocumentPaginator(
                string title, string subTitle, IList<ReportColumn> columns, List<object> rows,
                List<string> footerLines, bool useColor, double printableWidth, double printableHeight,
                Thickness margin, double baseFontSize, IList<ReportColumnGroup> columnGroups = null)
            {
                _title = title;
                _subTitle = subTitle;
                _columns = columns;
                _rows = rows;
                _footerLines = footerLines;
                _useColor = useColor;
                _pageSize = new Size(printableWidth, printableHeight);
                _margin = margin;

                _baseFontSize = baseFontSize;
                _titleFontSize = baseFontSize + 6;
                _headerFontSize = baseFontSize + 0.5;
                _footerFontSize = baseFontSize + 1.5;

                _rowHeight = baseFontSize + 6;
                _columnHeaderHeight = baseFontSize + 9;
                _footerLineHeight = baseFontSize + 6;
                _titleHeight = baseFontSize + 11;
                _subTitleHeight = baseFontSize + 4;
                _printedLineHeight = baseFontSize + 3;

                (_companyName, _companyAddress) = ReportCompanyHeader.GetLines();
                _companyNameFontSize = baseFontSize + 4;
                _companyAddressFontSize = baseFontSize;
                _companyNameHeight = _companyNameFontSize + 6;
                _companyAddressHeight = string.IsNullOrWhiteSpace(_companyAddress) ? 0 : _companyAddressFontSize + 5;

                _columnGroups = (columnGroups != null && columnGroups.Count > 0) ? columnGroups : null;
                _groupHeaderHeight = _columnGroups != null ? _headerFontSize + 12 : 0;
                _groupStartCol = new List<int>();
                _colExtraLeftPad = new double[_columns.Count];
                _colExtraRightPad = new double[_columns.Count];
                if (_columnGroups != null)
                {
                    int col = 0;
                    foreach (var g in _columnGroups)
                    {
                        _groupStartCol.Add(col);
                        col += g.ColumnCount;
                    }
                    // Every boundary except the very first (col 0, the left
                    // edge of the table, not a divider) gets extra padding
                    // on the column ending right before it and the column
                    // starting right after it.
                    for (int g = 1; g < _groupStartCol.Count; g++)
                    {
                        int afterIdx = _groupStartCol[g];
                        int beforeIdx = afterIdx - 1;
                        _colExtraRightPad[beforeIdx] += DividerExtraPad;
                        _colExtraLeftPad[afterIdx] += DividerExtraPad;
                    }
                }

                Type rowType = _rows.Count > 0 ? _rows[0].GetType() : null;
                _props = _columns.Select(c => rowType?.GetProperty(c.PropertyName, BindingFlags.Public | BindingFlags.Instance)).ToList();

                _groupStart = ComputeGroupStartFlags(_rows);

                double usableWidth = _pageSize.Width - _margin.Left - _margin.Right;
                double totalWeight = _columns.Sum(c => c.Width ?? 100);
                _colW = _columns.Select(c => (c.Width ?? 100) / totalWeight * usableWidth).ToArray();
                _colX = new double[_columns.Count];
                double x = _margin.Left;
                for (int i = 0; i < _columns.Count; i++)
                {
                    _colX[i] = x;
                    x += _colW[i];
                }

                _footerParsed = _footerLines.Select(SplitFooterLine).ToList();
                _footerColonWidth = MeasureTextWidth(":", _footerFontSize, Bold);
                _footerLabelColWidth = _footerParsed.Count > 0
                    ? _footerParsed.Max(f => MeasureTextWidth(f.Label, _footerFontSize, Bold))
                    : 0;
                _footerValueColWidth = _footerParsed.Count > 0
                    ? _footerParsed.Max(f => MeasureTextWidth(f.Value, _footerFontSize, Bold))
                    : 0;
            }

            // Shared logic: walks the full row list once, skipping rows with
            // a null ReportGroupKey entirely (they neither trigger emphasis
            // nor update "last seen key"), and flags every row whose key
            // differs from the last non-null key seen before it.
            private static bool[] ComputeGroupStartFlags(List<object> rows)
            {
                var flags = new bool[rows.Count];
                string lastKey = null;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] is not IReportRowEmphasis rr) continue;
                    string key = rr.ReportGroupKey;
                    if (key == null) continue;
                    flags[i] = key != lastKey;
                    lastKey = key;
                }
                return flags;
            }

            // Splits a "Label: value" footer line into its two parts. If there's no
            // colon, the whole thing is treated as the label with an empty value.
            private static (string Label, string Value) SplitFooterLine(string line)
            {
                if (string.IsNullOrEmpty(line)) return (string.Empty, string.Empty);
                int idx = line.IndexOf(':');
                return idx < 0
                    ? (line.Trim(), string.Empty)
                    : (line.Substring(0, idx).Trim(), line.Substring(idx + 1).Trim());
            }

            private static double MeasureTextWidth(string text, double fontSize, Typeface typeface)
            {
                var ft = new FormattedText(
                    text ?? "",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.Black,
                    null,
                    TextFormattingMode.Display,
                    VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
                return ft.Width;
            }

            public override DocumentPage GetPage(int pageNumber)
            {
                EnsurePaginated();
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    double y = _margin.Top;
                    bool isFirstPage = pageNumber == 0;
                    bool isLastPage = pageNumber == _pageStartRow.Count - 1;
                    double usableWidth = _pageSize.Width - _margin.Left - _margin.Right;

                    if (isFirstPage)
                    {
                        // Company Name / Address — centered, above the
                        // report title, nothing else on this line.
                        DrawText(dc, _companyName, _margin.Left, y, _companyNameFontSize, Bold, Brushes.Black,
                            TextAlignment.Center, usableWidth);
                        y += _companyNameHeight;

                        if (!string.IsNullOrWhiteSpace(_companyAddress))
                        {
                            DrawText(dc, _companyAddress, _margin.Left, y, _companyAddressFontSize, Regular, Brushes.Black,
                                TextAlignment.Center, usableWidth);
                            y += _companyAddressHeight;
                        }

                        y += 6;

                        DrawText(dc, _title, _margin.Left, y, _titleFontSize, Bold, Brushes.Black);
                        y += _titleHeight;
                        if (!string.IsNullOrWhiteSpace(_subTitle))
                        {
                            DrawText(dc, _subTitle, _margin.Left, y, _baseFontSize, Regular, Brushes.Gray);
                            y += _subTitleHeight;
                        }
                        DrawText(dc, $"Printed: {DateTime.Now:dd-MMM-yyyy hh:mm tt}", _margin.Left, y, _baseFontSize - 1, Regular, Brushes.Gray);
                        y += _printedLineHeight + 4;
                    }

                    // Group header row (e.g. "EXPENSES" / "INCOME") — drawn
                    // above the normal column headers on every page, same as
                    // the column headers themselves repeat per page. Top and
                    // bottom rules only, no left/right edges — the rest of
                    // the printed table has no outer vertical borders either
                    // (see the plain black text/column-header rule below),
                    // so a full box here would look like an unrelated,
                    // separately-framed element instead of a normal part of
                    // the same table. Always black & white — print output
                    // stays ink-cheap and avoids any contrast surprises
                    // depending on the printer, same reasoning as the rest
                    // of this helper (see the useColor comment at the top
                    // of Print()).
                    double tableTopY = y;
                    if (_columnGroups != null)
                    {
                        double tableLeft = _colX[0];
                        double tableRight = _colX[0] + _colW.Sum();
                        dc.DrawLine(new Pen(Brushes.Black, 1.0), new Point(tableLeft, y), new Point(tableRight, y));
                        dc.DrawLine(new Pen(Brushes.Black, 1.0), new Point(tableLeft, y + _groupHeaderHeight), new Point(tableRight, y + _groupHeaderHeight));

                        for (int g = 0; g < _columnGroups.Count; g++)
                        {
                            int startCol = _groupStartCol[g];
                            int span = _columnGroups[g].ColumnCount;
                            double groupX = _colX[startCol];
                            double groupW = _colW.Skip(startCol).Take(span).Sum();
                            DrawText(dc, _columnGroups[g].Header, groupX, y + 2, _headerFontSize + 3.5, Bold,
                                Brushes.Black, TextAlignment.Center, groupW - 6);
                        }
                        y += _groupHeaderHeight;
                    }

                    for (int c = 0; c < _columns.Count; c++)
                    {
                        DrawText(dc, _columns[c].Header, _colX[c] + _colExtraLeftPad[c], y, _headerFontSize, Bold,
                            Brushes.Black,
                            _columns[c].Alignment, _colW[c] - 6 - _colExtraLeftPad[c] - _colExtraRightPad[c]);
                    }
                    y += _columnHeaderHeight;

                    dc.DrawLine(new Pen(Brushes.Black, 0.75), new Point(_margin.Left, y), new Point(_pageSize.Width - _margin.Right, y));
                    y += 4;

                    int start = _pageStartRow[pageNumber];
                    int end = (pageNumber + 1 < _pageStartRow.Count) ? _pageStartRow[pageNumber + 1] : _rows.Count;

                    for (int i = start; i < end; i++)
                    {
                        var row = _rows[i];
                        bool highlighted = row is IReportRow rr && rr.IsReportHighlighted;
                        // Group-start (new date) emphasis is a smaller bump
                        // than a full Total-row highlight, and never stacks
                        // with it — a row is either the Total treatment or
                        // the group-start treatment, never both (a row that
                        // wants the former returns null from ReportGroupKey
                        // specifically to avoid this overlap — see
                        // DaybookLedgerRow.ReportGroupKey).
                        bool groupStart = !highlighted && _groupStart[i];
                        double extraBump = row is IReportRowEmphasis rrBump ? rrBump.ReportExtraFontBump : 0;
                        double fontSize = (highlighted ? _baseFontSize + 1.5 : groupStart ? _baseFontSize + 1.0 : _baseFontSize) + extraBump;
                        Typeface typeface = (highlighted || groupStart) ? Bold : Regular;
                        double maxRowHeight = _rowHeight;

                        // Alternating shading, skipped on highlighted rows
                        // (Day Total / date-header) so it doesn't compete
                        // with their existing bold/bigger treatment. Uses
                        // the base row height as an approximation — same
                        // estimate pagination already uses — since true
                        // wrapped height isn't known until the text below
                        // is measured.
                        if (!highlighted && i % 2 == 1)
                        {
                            double tableWidth = _colW.Sum();
                            dc.DrawRectangle(AltRowBg, null, new Rect(_colX[0] - 2, y - 1, tableWidth + 4, maxRowHeight + 2));
                        }

                        bool boxBorder = row is IReportRowEmphasis rrTopBorder && rrTopBorder.ReportRowBottomBorder;
                        if (boxBorder)
                        {
                            double topBorderY = y - 2;
                            dc.DrawLine(new Pen(Brushes.Black, 0.75),
                                new Point(_colX[0], topBorderY), new Point(_colX[0] + _colW.Sum(), topBorderY));
                        }

                        string mergedLabel = null;
                        int mergeSpan = row is IReportRow rrSpan ? rrSpan.GetMergedLabelSpan(out mergedLabel) : 0;

                        int startCol = 0;
                        if (mergeSpan != 0)
                        {
                            // Negative span = merge abs(span) columns
                            // left-aligned (e.g. Day Book account-head text
                            // filling the blanked-out Date column); positive
                            // = the original centered "TOTAL"-style merge.
                            int absSpan = Math.Abs(mergeSpan);
                            TextAlignment mergeAlign = mergeSpan < 0 ? TextAlignment.Left : TextAlignment.Center;
                            double mergedWidth = _colW.Take(absSpan).Sum() - 6;
                            double cellHeight = DrawText(dc, mergedLabel, _colX[0], y, fontSize, typeface, Brushes.Black,
                                mergeAlign, mergedWidth);
                            if (cellHeight > maxRowHeight) maxRowHeight = cellHeight;
                            startCol = absSpan;
                        }

                        for (int c = startCol; c < _columns.Count; c++)
                        {
                            string text = FormatValue(row, _props[c], _columns[c]);
                            double cellHeight = DrawText(dc, text, _colX[c] + _colExtraLeftPad[c], y, fontSize, typeface, Brushes.Black,
                                _columns[c].Alignment, _colW[c] - 6 - _colExtraLeftPad[c] - _colExtraRightPad[c]);
                            if (cellHeight > maxRowHeight) maxRowHeight = cellHeight;
                        }

                        if (row is IReportRowEmphasis rrBorder && rrBorder.ReportRowBottomBorder)
                        {
                            double borderY = y + maxRowHeight + 1;
                            dc.DrawLine(new Pen(Brushes.Black, 0.75),
                                new Point(_colX[0], borderY), new Point(_colX[0] + _colW.Sum(), borderY));
                        }

                        y += maxRowHeight + 2;
                    }

                    // Vertical divider(s) between column groups (e.g. the
                    // line separating EXPENSES from INCOME), spanning from
                    // the group header down through the last row drawn on
                    // this page — the printed equivalent of the black
                    // Border already separating the two SfDataGrids on
                    // screen. Skipped when the caller passed no groups.
                    if (_columnGroups != null)
                    {
                        for (int g = 1; g < _groupStartCol.Count; g++)
                        {
                            double dividerX = _colX[_groupStartCol[g]];
                            dc.DrawLine(new Pen(Brushes.Black, 1.5),
                                new Point(dividerX, tableTopY), new Point(dividerX, y));
                        }
                    }

                    if (isLastPage && _footerParsed.Count > 0)
                    {
                        y += 3;
                        dc.DrawLine(new Pen(Brushes.Black, 0.75), new Point(_margin.Left, y), new Point(_pageSize.Width - _margin.Right, y));
                        y += 8;

                        // Right-anchored label/colon/value block. The label and value
                        // columns are sized once (in the constructor) from the widest
                        // label/value across all footer lines, so every line's colon
                        // lands at the same X regardless of text length.
                        double blockRight = _pageSize.Width - _margin.Right;
                        double valueColLeft = blockRight - _footerValueColWidth;
                        double colonX = valueColLeft - FooterColumnGap - _footerColonWidth;
                        double labelColLeft = colonX - FooterColumnGap - _footerLabelColWidth;

                        foreach (var (label, value) in _footerParsed)
                        {
                            DrawText(dc, label, labelColLeft, y, _footerFontSize, Bold, Brushes.Black, TextAlignment.Left, _footerLabelColWidth);
                            DrawText(dc, ":", colonX, y, _footerFontSize, Bold, Brushes.Black, TextAlignment.Left, _footerColonWidth);
                            DrawText(dc, value, valueColLeft, y, _footerFontSize, Bold, Brushes.Black, TextAlignment.Right, _footerValueColWidth);
                            y += _footerLineHeight;
                        }
                    }
                }
                return new DocumentPage(visual, _pageSize, new Rect(new Point(0, 0), _pageSize), new Rect(new Point(0, 0), _pageSize));
            }

            private void EnsurePaginated()
            {
                if (_paginated) return;
                double headerBlockFirstPage = _companyNameHeight + _companyAddressHeight + 6
                    + _titleHeight + _subTitleHeight + _printedLineHeight + 4;
                double perPageChrome = _groupHeaderHeight + _columnHeaderHeight + 4;
                double usableFirst = _pageSize.Height - _margin.Top - _margin.Bottom - headerBlockFirstPage - perPageChrome;
                double usableOther = _pageSize.Height - _margin.Top - _margin.Bottom - perPageChrome;

                _pageStartRow.Add(0);
                int idx = 0;
                bool firstPage = true;

                while (idx < _rows.Count)
                {
                    double usable = firstPage ? usableFirst : usableOther;
                    // Use RowHeight * 1.5 as buffer for wrapping text estimates
                    int rowsThisPage = Math.Max(1, (int)(usable / (_rowHeight * 1.5)));
                    idx = Math.Min(idx + rowsThisPage, _rows.Count);
                    firstPage = false;
                    if (idx < _rows.Count) _pageStartRow.Add(idx);
                }
                _pageCount = _pageStartRow.Count;
                _paginated = true;
            }

            private static string FormatValue(object row, PropertyInfo prop, ReportColumn column)
            {
                if (column.ValueSelector == null && prop == null) return "";
                if (row is IReportRow rr && rr.IsReportFieldBlank(column.PropertyName)) return "";
                object value = column.ValueSelector != null ? column.ValueSelector(row) : prop.GetValue(row);
                if (value == null) return "";
                string format = column.Format;
                if (!string.IsNullOrEmpty(format) && value is IFormattable formattable) return formattable.ToString(format, null);
                if (value is DateTime dt) return dt.ToString("dd-MMM-yyyy");
                return value.ToString();
            }

            private static double DrawText(
    DrawingContext dc, string text, double x, double y, double fontSize,
    Typeface typeface, Brush foreground,
    TextAlignment alignment = TextAlignment.Left, double? maxWidth = null,
    Brush background = null, double backgroundHeight = 0)
            {
                // Use the modern 9-arg constructor: it takes TextFormattingMode,
                // not TextWrapping. Wrapping is controlled by MaxTextWidth below —
                // FormattedText wraps automatically once MaxTextWidth is set.
                var ft = new FormattedText(
                    text ?? "",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    foreground,
                    null, // NumberSubstitution
                    TextFormattingMode.Display,
                    VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);

                // FormattedText.Trimming defaults to CharacterEllipsis, which
                // truncates with "..." on a single line instead of wrapping —
                // that's what was cutting long item names short. We always
                // want a genuine multi-line wrap here, never ellipsis.
                ft.Trimming = TextTrimming.None;

                if (maxWidth.HasValue)
                {
                    ft.MaxTextWidth = Math.Max(1, maxWidth.Value);
                }

                double drawX = x;
                if (alignment == TextAlignment.Right && maxWidth.HasValue)
                    drawX = x + Math.Max(0, maxWidth.Value - ft.Width);
                else if (alignment == TextAlignment.Center && maxWidth.HasValue)
                    drawX = x + Math.Max(0, (maxWidth.Value - ft.Width) / 2);

                if (background != null)
                {
                    dc.DrawRectangle(background, null,
                        new Rect(x - 2, y - 1, (maxWidth ?? ft.Width) + 4, backgroundHeight));
                }

                dc.DrawText(ft, new Point(drawX, y));

                return ft.Height; // Return actual height for dynamic row calculation
            }

            public override bool IsPageCountValid => _paginated;
            public override int PageCount => _pageCount;
            public override Size PageSize { get => _pageSize; set { } }
            public override IDocumentPaginatorSource Source => null;
        }
    }
}