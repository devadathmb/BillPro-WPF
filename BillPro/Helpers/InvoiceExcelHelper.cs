using ClosedXML.Excel;
using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace BillPro.Helpers
{
    // ═══════════════════════════════════════════════════════════════════════
    // INVOICE EXCEL EXPORT
    // Sibling to InvoicePrintHelper — mirrors the same letterhead content
    // (company header incl. phones/email/reg no/bank blocks, invoice no /
    // date, bill-to/ship-to detail, item table, totals, net total box,
    // signatory) laid out as a spreadsheet instead of a QuestPDF page.
    // Column set matches InvoicePrintHelper.BuildItemTable exactly: a
    // single Tax % / Tax Amt pair (no CGST/SGST split). Reuses the same
    // InvoiceData built for Print/PDF (see SalesEntryView.BuildInvoiceData),
    // so there's one place that maps a bill to "what goes on the page" for
    // all three outputs.
    // ═══════════════════════════════════════════════════════════════════════
    public static class InvoiceExcelHelper
    {
        private static readonly XLColor HeaderBlue = XLColor.FromArgb(0x1D, 0x4E, 0xD8);
        private static readonly XLColor StripeBlue = XLColor.FromArgb(0xEF, 0xF6, 0xFF);

        private static readonly string[] Headers =
            { "SNo", "Particulars", "Rate", "Qty", "Disc Amt", "Taxable Value", "Tax %", "Tax Amt", "Total" };

        public static void Export(InvoiceData data, string suggestedFileName = null)
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = suggestedFileName ?? SanitizeFileName(data.FormattedBillNo) + ".xlsx"
            };

            if (saveDialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                using (var workbook = new XLWorkbook())
                {
                    var sheet = workbook.Worksheets.Add(SanitizeSheetName(data.FormattedBillNo));

                    // Column layout mirrors InvoicePrintHelper.DefineInvoiceColumns:
                    // SNo | Particulars | Rate | Qty | Disc Amt | Taxable Value | Tax % | Tax Amt | Total
                    int colCount = Headers.Length;
                    int qtyCol = 4;
                    int discCol = 5;
                    int taxableCol = 6;
                    int taxAmtCol = 8;
                    int totalCol = colCount;

                    int r = 1;

                    // ── Company header (name/address + phones/email, reg/bank right) ─
                    int companyBlockTop = r;

                    sheet.Cell(r, 1).Value = data.CompanyName;
                    sheet.Range(r, 1, r, Math.Max(1, colCount - 4)).Merge();
                    sheet.Cell(r, 1).Style.Font.Bold = true;
                    sheet.Cell(r, 1).Style.Font.FontSize = 18;
                    r++;

                    foreach (var line in data.CompanyAddressLines)
                    {
                        sheet.Cell(r, 1).Value = line;
                        sheet.Range(r, 1, r, Math.Max(1, colCount - 4)).Merge();
                        sheet.Cell(r, 1).Style.Font.Bold = true;
                        sheet.Cell(r, 1).Style.Font.FontSize = 9;
                        r++;
                    }

                    if (!string.IsNullOrWhiteSpace(data.CompanyPhone1))
                    {
                        string label1 = string.IsNullOrWhiteSpace(data.CompanyPhone1Label) ? "PHONE" : data.CompanyPhone1Label;
                        sheet.Cell(r, 1).Value = $"{label1}: {data.CompanyPhone1}";
                        sheet.Range(r, 1, r, Math.Max(1, colCount - 4)).Merge();
                        sheet.Cell(r, 1).Style.Font.Bold = true;
                        sheet.Cell(r, 1).Style.Font.FontSize = 9;
                        r++;
                    }
                    if (!string.IsNullOrWhiteSpace(data.CompanyPhone2))
                    {
                        string label2 = string.IsNullOrWhiteSpace(data.CompanyPhone2Label) ? "PHONE" : data.CompanyPhone2Label;
                        sheet.Cell(r, 1).Value = $"{label2}: {data.CompanyPhone2}";
                        sheet.Range(r, 1, r, Math.Max(1, colCount - 4)).Merge();
                        sheet.Cell(r, 1).Style.Font.Bold = true;
                        sheet.Cell(r, 1).Style.Font.FontSize = 9;
                        r++;
                    }
                    if (!string.IsNullOrWhiteSpace(data.CompanyEmail))
                    {
                        sheet.Cell(r, 1).Value = $"EMAIL: {data.CompanyEmail}";
                        sheet.Range(r, 1, r, Math.Max(1, colCount - 4)).Merge();
                        sheet.Cell(r, 1).Style.Font.Bold = true;
                        sheet.Cell(r, 1).Style.Font.FontSize = 9;
                        r++;
                    }

                    int companyBlockBottom = r - 1;

                    // Right-side block: Reg No + Bank details, aligned beside the company name block
                    bool hasBank1 = !string.IsNullOrWhiteSpace(data.CompanyBank1Name);
                    bool hasBank2 = !string.IsNullOrWhiteSpace(data.CompanyBank2Name);

                    if (!string.IsNullOrWhiteSpace(data.CompanyRegNumber) || hasBank1 || hasBank2)
                    {
                        int rr = companyBlockTop;
                        int rightCol = colCount - 3;

                        if (!string.IsNullOrWhiteSpace(data.CompanyRegNumber))
                        {
                            var cell = sheet.Cell(rr, rightCol);
                            cell.Value = $"REG. NO: {data.CompanyRegNumber}";
                            cell.Style.Font.Bold = true;
                            cell.Style.Font.FontSize = 9;
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                            sheet.Range(rr, rightCol, rr, colCount).Merge();
                            rr++;
                        }

                        if (hasBank1)
                            rr = WriteBankBlock(sheet, rr, rightCol, colCount, data.CompanyBank1Name, data.CompanyBank1AccountNo, data.CompanyBank1AccountHolder);
                        if (hasBank2)
                            rr = WriteBankBlock(sheet, rr, rightCol, colCount, data.CompanyBank2Name, data.CompanyBank2AccountNo, data.CompanyBank2AccountHolder);

                        companyBlockBottom = Math.Max(companyBlockBottom, rr - 1);
                    }

                    r = companyBlockBottom + 1;
                    sheet.Range(r - 1, 1, r - 1, colCount).Style.Border.BottomBorder = XLBorderStyleValues.Thin;

                    // ── Invoice title ─────────────────────────────────────
                    sheet.Cell(r, 1).Value = data.InvoiceTitle;
                    sheet.Range(r, 1, r, colCount).Merge();
                    sheet.Cell(r, 1).Style.Font.Bold = true;
                    sheet.Cell(r, 1).Style.Font.FontSize = 13;
                    sheet.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    r++;

                    // ── Bill To / Ship To side by side ───────────────────
                    sheet.Cell(r, 1).Value = "Details of Receiver (Billed to):";
                    sheet.Cell(r, 1).Style.Font.Bold = true;
                    sheet.Cell(r, 1).Style.Font.FontSize = 9;

                    int shipCol = (colCount / 2) + 1;
                    sheet.Cell(r, shipCol).Value = "Details of Receiver (Shipped to):";
                    sheet.Cell(r, shipCol).Style.Font.Bold = true;
                    sheet.Cell(r, shipCol).Style.Font.FontSize = 9;
                    r++;

                    int billEndRow = WritePartyBlock(sheet, r, 1, data.BillTo);
                    int shipEndRow = WritePartyBlock(sheet, r, shipCol, data.ShipTo);
                    r = Math.Max(billEndRow, shipEndRow);
                    sheet.Range(r - 1, 1, r - 1, colCount).Style.Border.BottomBorder = XLBorderStyleValues.Thin;

                    // ── Invoice No / Date (bigger, bold — matches print) ─
                    var invNoLabelCell = sheet.Cell(r, 1);
                    invNoLabelCell.Value = "INVOICE NO.";
                    invNoLabelCell.Style.Font.Bold = true;
                    invNoLabelCell.Style.Font.FontSize = 12;

                    var invNoValueCell = sheet.Cell(r, 2);
                    invNoValueCell.Value = data.FormattedBillNo;
                    invNoValueCell.Style.Font.Bold = true;
                    invNoValueCell.Style.Font.FontSize = 12;
                    sheet.Range(r, 2, r, 3).Merge();

                    var dateLabelCell = sheet.Cell(r, shipCol);
                    dateLabelCell.Value = "Date";
                    dateLabelCell.Style.Font.Bold = true;
                    dateLabelCell.Style.Font.FontSize = 12;

                    var dateValueCell = sheet.Cell(r, shipCol + 1);
                    dateValueCell.Value = data.BillDate;
                    dateValueCell.Style.Font.Bold = true;
                    dateValueCell.Style.Font.FontSize = 12;
                    r++;

                    if (!string.IsNullOrWhiteSpace(data.TransportMode) || !string.IsNullOrWhiteSpace(data.VehicleNo))
                    {
                        sheet.Cell(r, 1).Value = "Transportation Mode:";
                        sheet.Cell(r, 1).Style.Font.Bold = true;
                        sheet.Cell(r, 2).Value = data.TransportMode ?? "";
                        sheet.Range(r, 2, r, 3).Merge();

                        sheet.Cell(r, shipCol).Value = "Vehicle No.:";
                        sheet.Cell(r, shipCol).Style.Font.Bold = true;
                        sheet.Cell(r, shipCol + 1).Value = data.VehicleNo ?? "";
                        r++;
                    }

                    sheet.Range(r - 1, 1, r - 1, colCount).Style.Border.BottomBorder = XLBorderStyleValues.Thin;

                    // One blank row directly above the item table — the only intentional spacer row.
                    r++;

                    // ── Item table header ────────────────────────────────
                    int headerRow = r;
                    for (int c = 0; c < Headers.Length; c++)
                    {
                        var cell = sheet.Cell(headerRow, c + 1);
                        cell.Value = Headers[c];
                        cell.Style.Font.Bold = true;
                        cell.Style.Font.FontColor = XLColor.White;
                        cell.Style.Fill.BackgroundColor = HeaderBlue;
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                    r++;

                    int firstDataRow = r;
                    foreach (var line in data.Lines)
                    {
                        int c = 1;
                        sheet.Cell(r, c++).Value = line.SlNo;
                        sheet.Cell(r, c++).Value = line.Particulars;
                        SetNumeric(sheet.Cell(r, c++), line.Rate, "N2");
                        SetNumeric(sheet.Cell(r, c++), line.Qty, "N3");
                        SetNumeric(sheet.Cell(r, c++), line.DiscAmt, "N2");
                        SetNumeric(sheet.Cell(r, c++), line.TaxableValue, "N2");
                        SetNumeric(sheet.Cell(r, c++), line.TaxPercent, "N1");
                        SetNumeric(sheet.Cell(r, c++), line.TaxAmount, "N2");
                        SetNumeric(sheet.Cell(r, c++), line.Total, "N2");

                        sheet.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                        for (int cc = 1; cc <= Headers.Length; cc++)
                            sheet.Cell(r, cc).Style.Border.OutsideBorder = XLBorderStyleValues.Hair;

                        if ((r - firstDataRow) % 2 == 1)
                            for (int cc = 1; cc <= Headers.Length; cc++)
                                sheet.Cell(r, cc).Style.Fill.BackgroundColor = StripeBlue;

                        r++;
                    }

                    if (data.Lines.Count == 0)
                    {
                        sheet.Cell(r, 1).Value = "No items on this bill.";
                        sheet.Cell(r, 1).Style.Font.Italic = true;
                        r++;
                    }

                    // ── Totals row — each figure sits directly under its own column,
                    //    with matching borders/shading so it reads as part of the grid ──
                    double totalQty = data.Lines.Sum(l => l.Qty);
                    double totalDisc = data.Lines.Sum(l => l.DiscAmt);
                    double totalTaxable = data.Lines.Sum(l => l.TaxableValue);
                    double totalTax = data.Lines.Sum(l => l.TaxAmount);
                    double totalAmount = data.Lines.Sum(l => l.Total);

                    int totalsRow = r;
                    sheet.Cell(totalsRow, 2).Value = "Total";
                    SetNumeric(sheet.Cell(totalsRow, qtyCol), totalQty, "N3");
                    SetNumeric(sheet.Cell(totalsRow, discCol), totalDisc, "N2");
                    SetNumeric(sheet.Cell(totalsRow, taxableCol), totalTaxable, "N2");
                    SetNumeric(sheet.Cell(totalsRow, taxAmtCol), totalTax, "N2");
                    SetNumeric(sheet.Cell(totalsRow, totalCol), totalAmount, "N2");

                    for (int cc = 1; cc <= colCount; cc++)
                    {
                        var cell = sheet.Cell(totalsRow, cc);
                        cell.Style.Font.Bold = true;
                        cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.RightBorder = XLBorderStyleValues.Thin;
                    }
                    r++;

                    // ── E & O.E. / In words (left) — Rounded/Net Total box (right), matches print ──
                    int bottomBlockTop = r;

                    sheet.Cell(r, 1).Value = "E & O.E.";
                    sheet.Cell(r, 1).Style.Font.Bold = true;
                    r++;

                    sheet.Cell(r, 1).Value = "In Words:";
                    sheet.Cell(r, 1).Style.Font.Bold = true;
                    sheet.Cell(r, 2).Value = AmountInWordsConverter.Convert(data.NetTotal);
                    sheet.Range(r, 2, r, Math.Max(2, colCount - 3)).Merge();
                    r++;

                    int netBoxLeftCol = colCount - 2;

                    var roundedLabel = sheet.Cell(bottomBlockTop, netBoxLeftCol);
                    roundedLabel.Value = "Rounded Amt.";
                    var roundedValue = sheet.Cell(bottomBlockTop, colCount);
                    SetNumeric(roundedValue, data.RoundOff, "N2");

                    var netLabel = sheet.Cell(bottomBlockTop + 1, netBoxLeftCol);
                    netLabel.Value = "NET TOTAL";
                    netLabel.Style.Font.Bold = true;
                    var netValue = sheet.Cell(bottomBlockTop + 1, colCount);
                    SetNumeric(netValue, data.NetTotal, "N2");
                    netValue.Style.Font.Bold = true;
                    netValue.Style.Font.FontSize = 14;

                    var netBox = sheet.Range(bottomBlockTop, netBoxLeftCol, bottomBlockTop + 1, colCount);
                    netBox.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    for (int cc = netBoxLeftCol; cc <= colCount; cc++)
                        sheet.Cell(bottomBlockTop, cc).Style.Border.BottomBorder = XLBorderStyleValues.Thin;

                    r = Math.Max(r, bottomBlockTop + 2);

                    sheet.Cell(r, colCount - 1).Value = $"For {data.CompanyName}";
                    r++;
                    sheet.Cell(r, colCount - 1).Value = "Authorised Signatory.";

                    sheet.Columns().AdjustToContents();

                    workbook.SaveAs(saveDialog.FileName);
                }

                Mouse.OverrideCursor = null;

                var result = MessageBox.Show("Excel invoice created successfully.\n\nOpen it now?",
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
                MessageBox.Show("Error creating Excel invoice:\n" + ex.Message,
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private static int WriteBankBlock(IXLWorksheet sheet, int row, int leftCol, int rightCol, string bankName, string accNo, string accHolder)
        {
            var nameCell = sheet.Cell(row, leftCol);
            nameCell.Value = bankName ?? "";
            nameCell.Style.Font.Bold = true;
            nameCell.Style.Font.FontSize = 8;
            nameCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            sheet.Range(row, leftCol, row, rightCol).Merge();
            row++;

            if (!string.IsNullOrWhiteSpace(accNo))
            {
                var cell = sheet.Cell(row, leftCol);
                cell.Value = accNo;
                cell.Style.Font.FontSize = 8;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                sheet.Range(row, leftCol, row, rightCol).Merge();
                row++;
            }
            if (!string.IsNullOrWhiteSpace(accHolder))
            {
                var cell = sheet.Cell(row, leftCol);
                cell.Value = accHolder;
                cell.Style.Font.FontSize = 8;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                sheet.Range(row, leftCol, row, rightCol).Merge();
                row++;
            }

            return row;
        }

        private static int WritePartyBlock(IXLWorksheet sheet, int row, int col, InvoiceParty party)
        {
            sheet.Cell(row, col).Value = party.Name ?? "";
            sheet.Cell(row, col).Style.Font.Bold = true;
            sheet.Cell(row, col).Style.Font.FontSize = 10;
            row++;

            foreach (var line in party.AddressLines)
            {
                sheet.Cell(row, col).Value = line;
                sheet.Cell(row, col).Style.Font.FontSize = 9;
                row++;
            }

            row = WriteLabelValue(sheet, row, col, "State", party.State);
            row = WriteLabelValue(sheet, row, col, "State Code", party.StateCode);
            row = WriteLabelValue(sheet, row, col, "Phone", party.Phone);
            row = WriteLabelValue(sheet, row, col, "Mobile", party.Mobile);
            row = WriteLabelValue(sheet, row, col, "GSTIN", party.Gstin);

            return row;
        }

        private static int WriteLabelValue(IXLWorksheet sheet, int row, int col, string label, string value)
        {
            var labelCell = sheet.Cell(row, col);
            labelCell.Value = label + ":";
            labelCell.Style.Font.FontSize = 9;

            var valueCell = sheet.Cell(row, col + 1);
            valueCell.Value = value ?? "";
            valueCell.Style.Font.FontSize = 9;

            return row + 1;
        }

        private static void SetNumeric(IXLCell cell, double value, string format)
        {
            cell.Value = value;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            cell.Style.NumberFormat.Format = format switch
            {
                "N1" => "#,##0.0",
                "N2" => "#,##0.00",
                "N3" => "#,##0.000",
                _ => "#,##0.00"
            };
        }

        private static string SanitizeFileName(string name)
        {
            name ??= "Invoice";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(" ", "_");
        }

        private static string SanitizeSheetName(string name)
        {
            name ??= "Invoice";
            var invalid = new[] { '\\', '/', '?', '*', '[', ']', ':' };
            foreach (var c in invalid)
                name = name.Replace(c, '-');
            return name.Length > 31 ? name.Substring(0, 31) : name;
        }
    }
}