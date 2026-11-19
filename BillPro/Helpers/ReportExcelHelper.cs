using ClosedXML.Excel;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BillPro.Helpers
{
    // ═══════════════════════════════════════════════════════════════════════
    // REUSABLE REPORT EXCEL HELPER
    // Sibling to ReportPrintHelper — takes the SAME ReportColumn list and row
    // data, and exports an .xlsx instead of sending to a printer. Any report
    // that already built a `columns` list for Print gets Excel for free by
    // passing that same list here.
    //
    // Usage (see OpeningStockReportView.xaml.cs):
    //
    //   ReportExcelHelper.Export("OPENING STOCK REPORT (Batch-wise)", columns, _rows,
    //       footerLines: new[] { $"Total Qty: {totalQty:N3}", $"Total Stock Value: {totalAmount:N2}" });
    //
    // Requires the ClosedXML NuGet package (Install-Package ClosedXML).
    // ═══════════════════════════════════════════════════════════════════════
    public static class ReportExcelHelper
    {
        public static void Export(
            string title,
            IList<ReportColumn> columns,
            IEnumerable rows,
            IEnumerable<string> footerLines = null,
            string subTitle = null,
            string suggestedFileName = null,
            IList<ReportColumnGroup> columnGroups = null)
        {
            columnGroups = (columnGroups != null && columnGroups.Count > 0) ? columnGroups : null;
            Mouse.OverrideCursor = Cursors.Wait;

            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = suggestedFileName ?? SanitizeFileName(title) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
            };

            Mouse.OverrideCursor = null;   // release before the dialog shows — don't leave it looking stuck while the user picks a file

            if (saveDialog.ShowDialog() != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                using (var workbook = new XLWorkbook())
                {
                    var sheet = workbook.Worksheets.Add(SanitizeSheetName(title));
                    int currentRow = 1;

                    // ── Company Name / Address ──────────────────────────────
                    // Centered heading above everything else, including the
                    // report title. See ReportCompanyHeader for the source
                    // (Company Details / CompanyRepository).
                    var (companyName, companyAddress) = ReportCompanyHeader.GetLines();

                    sheet.Cell(currentRow, 1).Value = companyName;
                    sheet.Range(currentRow, 1, currentRow, columns.Count).Merge();
                    sheet.Cell(currentRow, 1).Style.Font.Bold = true;
                    sheet.Cell(currentRow, 1).Style.Font.FontSize = 15;
                    sheet.Cell(currentRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    currentRow++;

                    if (!string.IsNullOrWhiteSpace(companyAddress))
                    {
                        sheet.Cell(currentRow, 1).Value = companyAddress;
                        sheet.Range(currentRow, 1, currentRow, columns.Count).Merge();
                        sheet.Cell(currentRow, 1).Style.Font.FontSize = 10;
                        sheet.Cell(currentRow, 1).Style.Font.FontColor = XLColor.DimGray;
                        sheet.Cell(currentRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        currentRow++;
                    }

                    currentRow++; // blank spacer row before the report title

                    // ── Title ──────────────────────────────────────────────
                    sheet.Cell(currentRow, 1).Value = title;
                    sheet.Range(currentRow, 1, currentRow, columns.Count).Merge();
                    sheet.Cell(currentRow, 1).Style.Font.Bold = true;
                    sheet.Cell(currentRow, 1).Style.Font.FontSize = 14;
                    currentRow++;

                    if (!string.IsNullOrWhiteSpace(subTitle))
                    {
                        sheet.Cell(currentRow, 1).Value = subTitle;
                        sheet.Range(currentRow, 1, currentRow, columns.Count).Merge();
                        sheet.Cell(currentRow, 1).Style.Font.FontColor = XLColor.DimGray;
                        currentRow++;
                    }

                    sheet.Cell(currentRow, 1).Value = $"Printed: {DateTime.Now:dd-MMM-yyyy hh:mm tt}";
                    sheet.Range(currentRow, 1, currentRow, columns.Count).Merge();
                    sheet.Cell(currentRow, 1).Style.Font.FontSize = 9;
                    sheet.Cell(currentRow, 1).Style.Font.FontColor = XLColor.Gray;
                    currentRow += 2;

                    // ── Group header row (e.g. "EXPENSES" / "INCOME") ───────
                    // Merged cell per group, sitting above the normal column
                    // header row — matching the big section headings already
                    // shown above each SfDataGrid in the form. Skipped
                    // entirely when the caller passed no groups.
                    var groupStartCol = new List<int>();
                    if (columnGroups != null)
                    {
                        int gc = 0;
                        foreach (var g in columnGroups)
                        {
                            groupStartCol.Add(gc);
                            gc += g.ColumnCount;
                        }

                        for (int g = 0; g < columnGroups.Count; g++)
                        {
                            int startCol = groupStartCol[g] + 1; // 1-based
                            int endCol = startCol + columnGroups[g].ColumnCount - 1;
                            var groupRange = sheet.Range(currentRow, startCol, currentRow, endCol);
                            if (endCol > startCol) groupRange.Merge();
                            var groupCell = sheet.Cell(currentRow, startCol);
                            groupCell.Value = columnGroups[g].Header;
                            groupRange.Style.Font.Bold = true;
                            groupRange.Style.Font.FontSize = 13;
                            groupRange.Style.Fill.BackgroundColor = XLColor.FromArgb(0xDC, 0xE7, 0xFB);
                            groupRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                            groupRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                            groupRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        }
                        currentRow++;
                    }

                    // ── Header row ─────────────────────────────────────────
                    int headerRow = currentRow;
                    for (int c = 0; c < columns.Count; c++)
                    {
                        var cell = sheet.Cell(headerRow, c + 1);
                        cell.Value = columns[c].Header;
                        cell.Style.Font.Bold = true;
                        cell.Style.Font.FontColor = XLColor.White;
                        cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x1D, 0x4E, 0xD8);
                        cell.Style.Alignment.Horizontal = ToExcelAlignment(columns[c].Alignment);
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    }
                    currentRow++;

                    // ── Data rows ──────────────────────────────────────────
                    object[] rowArray = rows?.Cast<object>().ToArray() ?? Array.Empty<object>();
                    Type rowType = rowArray.Length > 0 ? rowArray[0].GetType() : null;

                    var propMap = new Dictionary<ReportColumn, PropertyInfo>();
                    if (rowType != null)
                    {
                        foreach (var col in columns)
                            propMap[col] = rowType.GetProperty(col.PropertyName, BindingFlags.Public | BindingFlags.Instance);
                    }

                    int firstDataRow = currentRow;

                    // Same skip-nulls logic as the Print/PDF helpers — see
                    // their ComputeGroupStartFlags for the full explanation.
                    bool[] groupStart = ComputeGroupStartFlags(rowArray);

                    for (int rowIdx = 0; rowIdx < rowArray.Length; rowIdx++)
                    {
                        var row = rowArray[rowIdx];
                        bool highlighted = row is IReportRow rr && rr.IsReportHighlighted;
                        bool isGroupStart = !highlighted && groupStart[rowIdx];
                        bool shaded = (currentRow - firstDataRow) % 2 == 1;
                        System.Windows.Media.Color? rowBg = row is IReportRowEmphasis rrBg ? rrBg.ReportRowBackground : null;
                        double extraBump = row is IReportRowEmphasis rrBump ? rrBump.ReportExtraFontBump : 0;

                        string mergedLabel = null;
                        int mergeSpan = row is IReportRow rrSpan ? rrSpan.GetMergedLabelSpan(out mergedLabel) : 0;
                        int absMergeSpan = Math.Abs(mergeSpan);
                        bool mergeLeftAlign = mergeSpan < 0;
                        int startCol = 0;

                        if (absMergeSpan > 1)
                        {
                            var mergedRange = sheet.Range(currentRow, 1, currentRow, absMergeSpan);
                            mergedRange.Merge();

                            var mergedCell = sheet.Cell(currentRow, 1);
                            mergedCell.Value = mergedLabel ?? string.Empty;
                            mergedRange.Style.Alignment.Horizontal = mergeLeftAlign
                                ? XLAlignmentHorizontalValues.Left
                                : XLAlignmentHorizontalValues.Center;
                            mergedRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                            mergedRange.Style.Border.OutsideBorder = XLBorderStyleValues.Hair;

                            if (highlighted || isGroupStart)
                            {
                                mergedRange.Style.Font.Bold = true;
                                mergedRange.Style.Font.FontSize += 1 + extraBump;
                            }

                            if (rowBg.HasValue)
                                mergedRange.Style.Fill.BackgroundColor = ToXlColor(rowBg.Value);
                            else if (shaded)
                                mergedRange.Style.Fill.BackgroundColor = XLColor.FromArgb(0xEF, 0xF6, 0xFF);

                            startCol = absMergeSpan;
                        }

                        for (int c = startCol; c < columns.Count; c++)
                        {
                            var col = columns[c];
                            propMap.TryGetValue(col, out var prop);
                            bool hasSource = col.ValueSelector != null || prop != null;
                            bool blank = !hasSource || (row is IReportRow rr2 && rr2.IsReportFieldBlank(col.PropertyName));
                            object value = blank ? null : (col.ValueSelector != null ? col.ValueSelector(row) : prop?.GetValue(row));

                            var cell = sheet.Cell(currentRow, c + 1);
                            SetCellValue(cell, value, col.Format);
                            cell.Style.Alignment.Horizontal = ToExcelAlignment(col.Alignment);
                            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Hair;

                            if (highlighted || isGroupStart)
                            {
                                cell.Style.Font.Bold = true;
                                cell.Style.Font.FontSize += 1 + extraBump;
                            }

                            if (rowBg.HasValue)
                                cell.Style.Fill.BackgroundColor = ToXlColor(rowBg.Value);
                            else if (shaded)
                                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0xEF, 0xF6, 0xFF);
                        }

                        if (row is IReportRowEmphasis rrBorder && rrBorder.ReportRowBottomBorder)
                        {
                            var borderRange = sheet.Range(currentRow, 1, currentRow, columns.Count);
                            borderRange.Style.Border.TopBorder = XLBorderStyleValues.Medium;
                            borderRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
                        }

                        currentRow++;
                    }

                    if (rowArray.Length == 0)
                    {
                        sheet.Cell(currentRow, 1).Value = "No records found.";
                        sheet.Cell(currentRow, 1).Style.Font.Italic = true;
                        sheet.Cell(currentRow, 1).Style.Font.FontColor = XLColor.Gray;
                        currentRow++;
                    }

                    // ── Divider(s) between column groups ────────────────────
                    // A thick left border running down the first column of
                    // every group after the first, from the group header row
                    // through the last data row — the spreadsheet equivalent
                    // of the black Border already separating the two
                    // SfDataGrids on screen.
                    if (columnGroups != null)
                    {
                        int dividerTopRow = headerRow - 1;
                        int dividerBottomRow = currentRow - 1;
                        for (int g = 1; g < groupStartCol.Count; g++)
                        {
                            int dividerCol = groupStartCol[g] + 1; // 1-based
                            var dividerRange = sheet.Range(dividerTopRow, dividerCol, dividerBottomRow, dividerCol);
                            dividerRange.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
                        }
                    }

                    // ── Footer summary lines ────────────────────────────────
                    // Each line is "Label: value". The label goes in the column
                    // immediately left of the value column, so every summary
                    // line's title sits directly beside its number, and — since
                    // that label column is the same for every line — they all
                    // line up underneath each other too.
                    if (footerLines != null)
                    {
                        currentRow++;
                        int labelCol = Math.Max(1, columns.Count - 1);
                        foreach (var line in footerLines)
                        {
                            var (label, value) = SplitFooterLine(line);

                            var labelCell = sheet.Cell(currentRow, labelCol);
                            labelCell.Value = label;
                            labelCell.Style.Font.Bold = true;
                            labelCell.Style.Font.FontSize = 12;
                            labelCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

                            var valueCell = sheet.Cell(currentRow, columns.Count);
                            valueCell.Value = value;
                            valueCell.Style.Font.Bold = true;
                            valueCell.Style.Font.FontSize = 12;
                            valueCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                            currentRow++;
                        }
                    }

                    sheet.Columns().AdjustToContents();
                    sheet.SheetView.FreezeRows(headerRow);

                    workbook.SaveAs(saveDialog.FileName);
                }

                Mouse.OverrideCursor = null;

                var result = MessageBox.Show("Excel file created successfully.\n\nOpen it now?",
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
                Mouse.OverrideCursor = null;
                MessageBox.Show("Error creating Excel file:\n" + ex.Message,
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static XLColor ToXlColor(System.Windows.Media.Color c) => XLColor.FromArgb(c.A, c.R, c.G, c.B);

        // Same skip-nulls logic as ReportPrintHelper/ReportPdfHelper's
        // private copies — see either for the full explanation.
        private static bool[] ComputeGroupStartFlags(object[] rows)
        {
            var flags = new bool[rows.Length];
            string lastKey = null;
            for (int i = 0; i < rows.Length; i++)
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

        private static void SetCellValue(IXLCell cell, object value, string format)
        {
            switch (value)
            {
                case null:
                    cell.Value = string.Empty;
                    break;
                case DateTime dt:
                    cell.Value = dt;
                    cell.Style.DateFormat.Format = "dd-mmm-yyyy";
                    break;
                case double d:
                    cell.Value = d;
                    if (!string.IsNullOrEmpty(format)) cell.Style.NumberFormat.Format = ToExcelNumberFormat(format);
                    break;
                case float f:
                    cell.Value = f;
                    if (!string.IsNullOrEmpty(format)) cell.Style.NumberFormat.Format = ToExcelNumberFormat(format);
                    break;
                case decimal dec:
                    cell.Value = dec;
                    if (!string.IsNullOrEmpty(format)) cell.Style.NumberFormat.Format = ToExcelNumberFormat(format);
                    break;
                case int i:
                    cell.Value = i;
                    break;
                case long l:
                    cell.Value = l;
                    break;
                case bool b:
                    cell.Value = b;
                    break;
                default:
                    cell.Value = value.ToString();
                    break;
            }
        }

        // Maps our .NET "N2" / "N3" style formats to Excel number-format codes.
        private static string ToExcelNumberFormat(string netFormat)
        {
            switch (netFormat)
            {
                case "N0": return "#,##0";
                case "N2": return "#,##0.00";
                case "N3": return "#,##0.000";
                case "N4": return "#,##0.0000";
                default: return "#,##0.00";
            }
        }

        private static XLAlignmentHorizontalValues ToExcelAlignment(System.Windows.TextAlignment alignment)
        {
            switch (alignment)
            {
                case System.Windows.TextAlignment.Right: return XLAlignmentHorizontalValues.Right;
                case System.Windows.TextAlignment.Center: return XLAlignmentHorizontalValues.Center;
                default: return XLAlignmentHorizontalValues.Left;
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(" ", "_");
        }

        private static string SanitizeSheetName(string name)
        {
            // Excel sheet names: max 31 chars, no  \ / ? * [ ] :
            var invalid = new[] { '\\', '/', '?', '*', '[', ']', ':' };
            foreach (var c in invalid)
                name = name.Replace(c, '-');
            return name.Length > 31 ? name.Substring(0, 31) : name;
        }
    }
}