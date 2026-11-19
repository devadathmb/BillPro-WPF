using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace BillPro.Helpers
{
    // ═══════════════════════════════════════════════════════════════════════
    // REUSABLE REPORT PDF HELPER
    // Sibling to ReportPrintHelper (always black & white, goes to a physical
    // printer) and ReportExcelHelper (spreadsheet). This one always renders
    // in color with alternating row shading — matching the Excel export —
    // and saves a .pdf file instead of printing.
    //
    // Requires the PdfSharp NuGet package (Install-Package PdfSharp).
    //
    // Usage (see OpeningStockReportView.xaml.cs):
    //
    //   ReportPdfHelper.Export("OPENING STOCK REPORT (Batch-wise)", columns, _rows,
    //       footerLines: new[] { $"Total Qty: {totalQty:N3}", $"Total Stock Value: {totalAmount:N2}" });
    // ═══════════════════════════════════════════════════════════════════════
    public static class ReportPdfHelper
    {
        private static readonly XColor HeaderBg = XColor.FromArgb(0x1D, 0x4E, 0xD8);
        private static readonly XColor AltRowBg = XColor.FromArgb(0xEF, 0xF6, 0xFF);
        private static readonly XColor GrayText = XColor.FromArgb(0x6B, 0x72, 0x80);

        private static bool _fontResolverRegistered;

        /// <summary>
        /// PdfSharp 6.x has no built-in font discovery (it dropped GDI+), so it
        /// needs to be told explicitly where "Segoe UI" lives. This reads the
        /// .ttf files straight out of the Windows Fonts folder. Call once,
        /// before the first XFont is created.
        /// </summary>
        private static void EnsureFontResolver()
        {
            if (_fontResolverRegistered) return;
            GlobalFontSettings.FontResolver = new SegoeUiFontResolver();
            _fontResolverRegistered = true;
        }

        private sealed class SegoeUiFontResolver : IFontResolver
        {
            public string DefaultFontName => "Segoe UI";

            public byte[] GetFont(string faceName)
            {
                string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                string fileName = faceName switch
                {
                    "SegoeUI#b" => "segoeuib.ttf",   // bold
                    "SegoeUI#i" => "segoeuiz.ttf",   // italic (Segoe UI's italic file is "z")
                    "SegoeUI#bi" => "segoeuiz.ttf",
                    _ => "segoeui.ttf",    // regular
                };

                string path = System.IO.Path.Combine(fontsDir, fileName);
                if (!System.IO.File.Exists(path))
                    path = System.IO.Path.Combine(fontsDir, "segoeui.ttf"); // fall back to regular

                return System.IO.File.ReadAllBytes(path);
            }

            public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
            {
                string face = (isBold, isItalic) switch
                {
                    (true, true) => "SegoeUI#bi",
                    (true, false) => "SegoeUI#b",
                    (false, true) => "SegoeUI#i",
                    _ => "SegoeUI",
                };
                return new FontResolverInfo(face);
            }
        }

        public static void Export(
            string title,
            IList<ReportColumn> columns,
            IEnumerable rows,
            IEnumerable<string> footerLines = null,
            string subTitle = null,
            string suggestedFileName = null,
            bool landscape = true,
            double baseFontSize = 9.0,
            IList<ReportColumnGroup> columnGroups = null)
        {
            if (columns == null) throw new ArgumentNullException(nameof(columns));

            EnsureFontResolver();

            var saveDialog = new SaveFileDialog
            {
                Filter = "PDF Document (*.pdf)|*.pdf",
                FileName = suggestedFileName ?? SanitizeFileName(title) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
            };

            if (saveDialog.ShowDialog() != true)
                return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var rowList = rows?.Cast<object>().ToList() ?? new List<object>();
                var footerList = footerLines?.ToList() ?? new List<string>();

                RenderPdf(saveDialog.FileName, title, subTitle, columns, rowList, footerList, landscape, baseFontSize, columnGroups);

                Mouse.OverrideCursor = null;

                var result = MessageBox.Show("PDF file created successfully.\n\nOpen it now?",
                    "Export Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(saveDialog.FileName)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error creating PDF file:\n" + ex.Message,
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private static void RenderPdf(
            string filePath, string title, string subTitle, IList<ReportColumn> columns,
            List<object> rows, List<string> footerLines, bool landscape, double baseFontSize,
            IList<ReportColumnGroup> columnGroups = null)
        {
            columnGroups = (columnGroups != null && columnGroups.Count > 0) ? columnGroups : null;
            var groupStartCol = new List<int>();
            if (columnGroups != null)
            {
                int gc = 0;
                foreach (var g in columnGroups)
                {
                    groupStartCol.Add(gc);
                    gc += g.ColumnCount;
                }
            }
            var groupHeaderFont = new XFont("Segoe UI", baseFontSize + 4, XFontStyleEx.Bold);
            var groupHeaderBg = XColor.FromArgb(0xDC, 0xE7, 0xFB);
            double groupHeaderHeight = columnGroups != null ? groupHeaderFont.Height + 12 : 0;

            // Extra breathing room on either side of a group divider so
            // cell text doesn't sit flush against the line — same reasoning
            // (and same padding amount) as ReportPrintHelper's identical
            // arrays. Zero for every column except the one right before /
            // right after a divider.
            var colExtraLeftPad = new double[columns.Count];
            var colExtraRightPad = new double[columns.Count];
            const double dividerExtraPad = 10;
            if (columnGroups != null)
            {
                for (int g = 1; g < groupStartCol.Count; g++)
                {
                    int afterIdx = groupStartCol[g];
                    int beforeIdx = afterIdx - 1;
                    colExtraRightPad[beforeIdx] += dividerExtraPad;
                    colExtraLeftPad[afterIdx] += dividerExtraPad;
                }
            }

            // Company Name / Address heading — centered above the report
            // title, first page only. See ReportCompanyHeader for the
            // source (Company Details / CompanyRepository).
            var (companyName, companyAddress) = ReportCompanyHeader.GetLines();
            var companyNameFont = new XFont("Segoe UI", baseFontSize + 5, XFontStyleEx.Bold);
            var companyAddressFont = new XFont("Segoe UI", baseFontSize, XFontStyleEx.Regular);

            var titleFont = new XFont("Segoe UI", baseFontSize + 6, XFontStyleEx.Bold);
            var subTitleFont = new XFont("Segoe UI", baseFontSize, XFontStyleEx.Regular);
            var printedFont = new XFont("Segoe UI", baseFontSize - 1, XFontStyleEx.Regular);
            var headerFont = new XFont("Segoe UI", baseFontSize + 0.5, XFontStyleEx.Bold);
            var cellFont = new XFont("Segoe UI", baseFontSize, XFontStyleEx.Regular);
            var cellFontBold = new XFont("Segoe UI", baseFontSize + 1.5, XFontStyleEx.Bold);
            var cellFontGroupStart = new XFont("Segoe UI", baseFontSize + 1.0, XFontStyleEx.Bold);
            var cellFontDateHeader = new XFont("Segoe UI", baseFontSize + 3.0, XFontStyleEx.Bold);
            var footerFont = new XFont("Segoe UI", baseFontSize + 1.5, XFontStyleEx.Bold);

            double rowHeight = baseFontSize + 8;
            double headerRowHeight = baseFontSize + 10;
            double margin = 30;

            Type rowType = rows.Count > 0 ? rows[0].GetType() : null;
            var props = columns.Select(c => rowType?.GetProperty(c.PropertyName, BindingFlags.Public | BindingFlags.Instance)).ToList();

            var document = new PdfDocument();
            document.Info.Title = title;

            PdfPage page = null;
            XGraphics gfx = null;
            double pageWidth = 0, pageHeight = 0;
            double[] colX = null, colW = null;
            double y = 0;
            double tableTopY = 0;

            void StartNewPage(bool isFirstPage)
            {
                page = document.AddPage();
                page.Orientation = landscape ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
                gfx = XGraphics.FromPdfPage(page);
                pageWidth = page.Width.Point;
                pageHeight = page.Height.Point;

                double usableWidth = pageWidth - margin * 2;
                double totalWeight = columns.Sum(c => c.Width ?? 100);
                colW = columns.Select(c => (c.Width ?? 100) / totalWeight * usableWidth).ToArray();
                colX = new double[columns.Count];
                double x = margin;
                for (int i = 0; i < columns.Count; i++)
                {
                    colX[i] = x;
                    x += colW[i];
                }

                y = margin;

                if (isFirstPage)
                {
                    var companyNameRect = new XRect(margin, y, usableWidth, companyNameFont.Height + 4);
                    gfx.DrawString(companyName, companyNameFont, XBrushes.Black, companyNameRect, XStringFormats.TopCenter);
                    y += companyNameFont.Height + 4;

                    if (!string.IsNullOrWhiteSpace(companyAddress))
                    {
                        var companyAddressRect = new XRect(margin, y, usableWidth, companyAddressFont.Height + 3);
                        gfx.DrawString(companyAddress, companyAddressFont, new XSolidBrush(GrayText), companyAddressRect, XStringFormats.TopCenter);
                        y += companyAddressFont.Height + 3;
                    }

                    y += 6;

                    gfx.DrawString(title, titleFont, XBrushes.Black, new XPoint(margin, y + titleFont.Height));
                    y += titleFont.Height + 4;

                    if (!string.IsNullOrWhiteSpace(subTitle))
                    {
                        gfx.DrawString(subTitle, subTitleFont, new XSolidBrush(GrayText), new XPoint(margin, y + subTitleFont.Height));
                        y += subTitleFont.Height + 2;
                    }

                    gfx.DrawString($"Printed: {DateTime.Now:dd-MMM-yyyy hh:mm tt}", printedFont,
                        new XSolidBrush(GrayText), new XPoint(margin, y + printedFont.Height));
                    y += printedFont.Height + 8;
                }

                DrawHeaderRow();
            }

            void DrawHeaderRow()
            {
                if (columnGroups != null)
                {
                    for (int g = 0; g < columnGroups.Count; g++)
                    {
                        int startCol = groupStartCol[g];
                        int span = columnGroups[g].ColumnCount;
                        double gx = colX[startCol];
                        double gw = colW.Skip(startCol).Take(span).Sum();
                        gfx.DrawRectangle(new XSolidBrush(groupHeaderBg), gx, y, gw, groupHeaderHeight);
                        var grect = new XRect(gx + 3, y, gw - 6, groupHeaderHeight);
                        gfx.DrawString(columnGroups[g].Header, groupHeaderFont, XBrushes.Black, grect, XStringFormats.TopCenter);
                    }
                    y += groupHeaderHeight;
                }

                gfx.DrawRectangle(new XSolidBrush(HeaderBg), margin, y, pageWidth - margin * 2, headerRowHeight);
                for (int c = 0; c < columns.Count; c++)
                {
                    var rect = new XRect(colX[c] + 3 + colExtraLeftPad[c], y,
                        colW[c] - 6 - colExtraLeftPad[c] - colExtraRightPad[c], headerRowHeight);
                    gfx.DrawString(columns[c].Header, headerFont, XBrushes.White, rect, ToXStringFormat(columns[c].Alignment));
                }
                y += headerRowHeight;
                tableTopY = y;

                if (columnGroups != null)
                {
                    double headerTop = tableTopY - headerRowHeight - groupHeaderHeight;
                    var dividerPen = new XPen(XColors.Black, 1.5);
                    for (int g = 1; g < groupStartCol.Count; g++)
                    {
                        double dividerX = colX[groupStartCol[g]];
                        gfx.DrawLine(dividerPen, dividerX, headerTop, dividerX, tableTopY);
                    }
                }
            }

            StartNewPage(true);

            double lineHeight = cellFont.GetHeight() + 2;

            // See ComputeGroupStartFlags for the shared skip-nulls logic —
            // identical to ReportPrintHelper's, computed once over the full
            // row list (not per-page) since a page boundary shouldn't reset
            // which row is considered the start of a new group.
            bool[] groupStart = ComputeGroupStartFlags(rows);

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                bool highlighted = row is IReportRow rr && rr.IsReportHighlighted;
                bool isGroupStart = !highlighted && groupStart[i];
                double extraBump = row is IReportRowEmphasis rrBump ? rrBump.ReportExtraFontBump : 0;
                XFont rowFont = (highlighted && extraBump > 0) ? cellFontDateHeader
                    : highlighted ? cellFontBold
                    : isGroupStart ? cellFontGroupStart
                    : cellFont;

                string mergedLabel = null;
                int mergeSpan = row is IReportRow rrSpan ? rrSpan.GetMergedLabelSpan(out mergedLabel) : 0;
                int absMergeSpan = Math.Abs(mergeSpan);
                bool mergeLeftAlign = mergeSpan < 0;
                double mergedWidth = absMergeSpan > 1
                    ? colX[absMergeSpan - 1] + colW[absMergeSpan - 1] - colX[0] - 6
                    : 0;
                List<string> mergedLines = absMergeSpan > 1 ? WrapText(gfx, mergedLabel, rowFont, mergedWidth) : null;

                // Wrap each column's text to its column width first, so we know
                // the row's real height before drawing anything (and before
                // deciding whether it fits on the current page). Columns inside
                // the merged span are skipped here — they're drawn as one cell.
                var cellLines = new List<string>[columns.Count];
                int maxLines = mergedLines?.Count ?? 1;
                for (int c = absMergeSpan > 1 ? absMergeSpan : 0; c < columns.Count; c++)
                {
                    string text = FormatValue(row, props[c], columns[c]);
                    var lines = WrapText(gfx, text, rowFont, colW[c] - 6 - colExtraLeftPad[c] - colExtraRightPad[c]);
                    cellLines[c] = lines;
                    if (lines.Count > maxLines) maxLines = lines.Count;
                }

                double thisRowHeight = Math.Max(rowHeight, maxLines * lineHeight + 4);

                if (y + thisRowHeight > pageHeight - margin - 30)
                {
                    StartNewPage(false);
                }

                // Row background: an explicit ReportRowBackground (e.g. the
                // Opening Balance row's amber tint, or an extra tint on a
                // Total row) always wins over the normal alternating
                // stripe. Falls back to the plain odd/even shading for
                // every ordinary row, same as before.
                XColor? rowBg = (row is IReportRowEmphasis rrBg ? rrBg.ReportRowBackground : null) is System.Windows.Media.Color wpfColor
                    ? XColor.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B)
                    : (i % 2 == 1 ? AltRowBg : (XColor?)null);

                if (rowBg.HasValue)
                    gfx.DrawRectangle(new XSolidBrush(rowBg.Value), margin, y, pageWidth - margin * 2, thisRowHeight);

                if (row is IReportRowEmphasis rrTopBorder && rrTopBorder.ReportRowBottomBorder)
                {
                    double topBorderY = y - 1;
                    gfx.DrawLine(XPens.Black, colX[0], topBorderY, colX[0] + colW.Sum(), topBorderY);
                }

                if (absMergeSpan > 1)
                {
                    double lineY = y + 2;
                    var mergeFormat = mergeLeftAlign ? XStringFormats.TopLeft : XStringFormats.TopCenter;
                    foreach (var line in mergedLines)
                    {
                        var rect = new XRect(colX[0] + 3, lineY, mergedWidth, lineHeight);
                        gfx.DrawString(line, rowFont, XBrushes.Black, rect, mergeFormat);
                        lineY += lineHeight;
                    }
                }

                for (int c = absMergeSpan > 1 ? absMergeSpan : 0; c < columns.Count; c++)
                {
                    double lineY = y + 2;
                    foreach (var line in cellLines[c])
                    {
                        var rect = new XRect(colX[c] + 3 + colExtraLeftPad[c], lineY,
                            colW[c] - 6 - colExtraLeftPad[c] - colExtraRightPad[c], lineHeight);
                        gfx.DrawString(line, rowFont, XBrushes.Black, rect, ToXStringFormat(columns[c].Alignment));
                        lineY += lineHeight;
                    }
                }

                if (row is IReportRowEmphasis rrBorder && rrBorder.ReportRowBottomBorder)
                {
                    double borderY = y + thisRowHeight - 1;
                    gfx.DrawLine(XPens.Black, colX[0], borderY, colX[0] + colW.Sum(), borderY);
                }

                // Vertical divider(s) between column groups, drawn for this
                // row's slice only — the row loop already handles page
                // breaks above (StartNewPage resets tableTopY), so drawing
                // per-row keeps each page's divider confined to that page's
                // own content instead of running off the bottom.
                if (columnGroups != null)
                {
                    var dividerPen = new XPen(XColors.Black, 1.5);
                    for (int g = 1; g < groupStartCol.Count; g++)
                    {
                        double dividerX = colX[groupStartCol[g]];
                        gfx.DrawLine(dividerPen, dividerX, y, dividerX, y + thisRowHeight);
                    }
                }

                y += thisRowHeight;
            }

            if (footerLines.Count > 0)
            {
                if (y + footerLines.Count * (footerFont.Height + 4) > pageHeight - margin)
                    StartNewPage(false);

                y += 6;
                gfx.DrawLine(XPens.Black, margin, y, pageWidth - margin, y);
                y += 10;

                // Each line is "Label: value". Split them and size the label/value
                // columns from the widest of each across all lines, so the colons
                // land at the same X on every line regardless of text length.
                var footerParsed = footerLines.Select(SplitFooterLine).ToList();
                double colonWidth = gfx.MeasureString(":", footerFont).Width;
                double labelColWidth = footerParsed.Max(f => gfx.MeasureString(f.Label, footerFont).Width);
                double valueColWidth = footerParsed.Max(f => gfx.MeasureString(f.Value, footerFont).Width);
                const double footerColumnGap = 6;

                double blockRight = pageWidth - margin;
                double valueColLeft = blockRight - valueColWidth;
                double colonX = valueColLeft - footerColumnGap - colonWidth;
                double labelColLeft = colonX - footerColumnGap - labelColWidth;

                foreach (var (label, value) in footerParsed)
                {
                    gfx.DrawString(label, footerFont, XBrushes.Black,
                        new XRect(labelColLeft, y, labelColWidth, footerFont.Height + 4), XStringFormats.TopLeft);
                    gfx.DrawString(":", footerFont, XBrushes.Black,
                        new XRect(colonX, y, colonWidth, footerFont.Height + 4), XStringFormats.TopLeft);
                    gfx.DrawString(value, footerFont, XBrushes.Black,
                        new XRect(valueColLeft, y, valueColWidth, footerFont.Height + 4), XStringFormats.TopRight);
                    y += footerFont.Height + 6;
                }
            }

            document.Save(filePath);
        }

        // Same skip-nulls logic as ReportPrintHelper's private copy — kept
        // duplicated rather than shared, since the two helpers' row-loop
        // shapes (paginated vs. single-pass) don't share a common base to
        // hang a shared implementation off of without more restructuring
        // than this feature is worth.
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

        /// <summary>
        /// Splits text into lines that each fit within maxWidth, word-wrapping
        /// on spaces. A single "word" longer than maxWidth on its own (e.g. a
        /// code with no spaces) is hard-broken character by character instead
        /// of being left to overflow the column.
        /// </summary>
        private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                lines.Add("");
                return lines;
            }

            if (maxWidth <= 0) maxWidth = 1;

            foreach (var paragraph in text.Split('\n'))
            {
                string current = "";
                foreach (var word in paragraph.Split(' '))
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;

                    if (gfx.MeasureString(candidate, font).Width <= maxWidth)
                    {
                        current = candidate;
                        continue;
                    }

                    // Candidate doesn't fit — flush what we had, then decide
                    // whether the new word itself needs hard-breaking.
                    if (current.Length > 0)
                    {
                        lines.Add(current);
                        current = "";
                    }

                    if (gfx.MeasureString(word, font).Width <= maxWidth)
                    {
                        current = word;
                    }
                    else
                    {
                        // Word alone is wider than the column — break it up
                        // character by character (handles long codes/strings
                        // with no spaces at all, like a pasted key or SKU).
                        string chunk = "";
                        foreach (char ch in word)
                        {
                            string test = chunk + ch;
                            if (gfx.MeasureString(test, font).Width <= maxWidth)
                            {
                                chunk = test;
                            }
                            else
                            {
                                if (chunk.Length > 0) lines.Add(chunk);
                                chunk = ch.ToString();
                            }
                        }
                        current = chunk;
                    }
                }

                if (current.Length > 0)
                    lines.Add(current);
            }

            return lines.Count > 0 ? lines : new List<string> { "" };
        }

        private static XStringFormat ToXStringFormat(System.Windows.TextAlignment alignment)
        {
            switch (alignment)
            {
                case System.Windows.TextAlignment.Right: return XStringFormats.TopRight;
                case System.Windows.TextAlignment.Center: return XStringFormats.TopCenter;
                default: return XStringFormats.TopLeft;
            }
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

        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(" ", "_");
        }
    }
}