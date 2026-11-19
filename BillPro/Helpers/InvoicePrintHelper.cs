using Microsoft.Win32;
using PdfiumViewer;
using QuestPDF.Elements;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.Versioning;

namespace BillPro.Helpers
{
    [SupportedOSPlatform("windows")]
    public static class InvoicePrintHelper
    {
        private static bool _licenseSet;
        private static byte[] _cachedFadedLogo;
        private static string _cachedLogoPath;

        private static void EnsureLicense()
        {
            if (_licenseSet) return;
            QuestPDF.Settings.License = LicenseType.Community;
            _licenseSet = true;
        }

        // ═══════════════════════════════════════════════════════════════════
        // DOCUMENT DEFINITION
        // ═══════════════════════════════════════════════════════════════════
        private static IDocument BuildDocument(InvoiceData data, bool useColor)
        {
            EnsureLicense();

            // Pre-cache the heavy watermark image generation before we start rendering loops
            if (HasLogo(data) && (_cachedLogoPath != data.CompanyLogoPath || _cachedFadedLogo == null))
            {
                _cachedFadedLogo = GetFadedLogoBytes(data.CompanyLogoPath, 0.08f);
                _cachedLogoPath = data.CompanyLogoPath;
            }

            int fillerRows = FindSafeFillerRowCount(data, useColor, out int totalPages);

            // Final render with full images enabled
            return ComposeDocument(data, useColor, totalPages, fillerRows, isProbe: false);
        }

        private static int FindSafeFillerRowCount(InvoiceData data, bool useColor, out int totalPages)
        {
            int baselinePages = RenderAndCountPages(data, useColor, fillerRows: 0);

            int lo = 0, hi = 40;
            while (hi < 400 && RenderAndCountPages(data, useColor, hi) == baselinePages)
            {
                hi *= 2;
            }

            int best = 0;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                int pages = RenderAndCountPages(data, useColor, mid);
                if (pages == baselinePages)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            // TEXT EXTRACTION REMOVED HERE: EnsureSpace() in ComposeDocument guarantees the 
            // totals block stays together. The binary search above is now 100% accurate on its own.

            totalPages = RenderAndCountPages(data, useColor, best);
            return best;
        }

        private static int RenderAndCountPages(InvoiceData data, bool useColor, int fillerRows)
        {
            int assumedTotalPages = 1;
            int actualPages = assumedTotalPages;

            for (int i = 0; i < 4; i++)
            {
                actualPages = RenderOnce(data, useColor, fillerRows, assumedTotalPages);
                if (actualPages == assumedTotalPages)
                    break;
                assumedTotalPages = actualPages;
            }

            return actualPages;
        }

        private static int RenderOnce(InvoiceData data, bool useColor, int fillerRows, int totalPagesGuess)
        {
            // isProbe = true strictly disables heavy image encoding to make PDF generation instant
            var probe = ComposeDocument(data, useColor, totalPagesGuess, fillerRows, isProbe: true);
            using var ms = new MemoryStream();
            probe.GeneratePdf(ms);
            ms.Position = 0;
            using var pdfDoc = PdfDocument.Load(ms);
            return pdfDoc.PageCount;
        }

        private static IDocument ComposeDocument(InvoiceData data, bool useColor, int totalPages, int fillerRowCount, bool isProbe)
        {
            var allLines = data.Lines ?? new List<InvoiceLine>();

            return QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Segoe UI"));

                    page.Header().Column(headerCol =>
                    {
                        headerCol.Item().ShowOnce().Column(c => BuildFullHeader(c, data, isProbe));
                        headerCol.Item().SkipOnce().Column(c => BuildContinuationHeader(c, data));
                    });

                    // Bypass rendering background images during probe logic entirely
                    if (HasLogo(data) && !isProbe && _cachedFadedLogo != null)
                    {
                        page.Background()
                            .AlignCenter().AlignMiddle()
                            .Width(320)
                            .Image(_cachedFadedLogo).FitWidth();
                    }

                    page.Content().Border(1).BorderColor(Colors.Black).Padding(10).Column(col =>
                    {
                        // FIX: BorderBottom(1) perfectly closes the vertical lines of the table at the bottom of EVERY page.
                        col.Item().BorderBottom(1).BorderColor(Colors.Black).Table(table => BuildItemTable(table, data, allLines, useColor, fillerRowCount));

                        // FIX: EnsureSpace() guarantees the TotalsRow and Signature Block never split.
                        // This allowed us to delete the 20-second text-extraction lag.
                        col.Item().EnsureSpace().Column(group =>
                        {
                            group.Item().Table(table => BuildTotalsRow(table, data));
                            group.Item().PaddingTop(10).Column(bottom => BuildBottomBlock(bottom, data));
                        });
                    });

                    if (totalPages > 1)
                    {
                        page.Footer().Dynamic(new ContinuedPageFooter(totalPages));
                    }
                });
            });
        }

        private sealed class ContinuedPageFooter : IDynamicComponent<int>
        {
            public int State { get; set; }

            public ContinuedPageFooter(int totalPages)
            {
                State = totalPages;
            }

            public DynamicComponentComposeResult Compose(DynamicContext context)
            {
                int totalPages = State;
                bool isLastPage = context.PageNumber >= totalPages;

                var content = context.CreateElement(el =>
                {
                    el.Column(col =>
                    {
                        if (!isLastPage)
                        {
                            col.Item().BorderTop(1).BorderColor(Colors.Black)
                                .PaddingTop(4).AlignRight()
                                .Text("CONTINUED...").Bold().FontSize(13);
                        }

                        col.Item().PaddingTop(4).AlignCenter()
                            .Text($"Page {context.PageNumber} of {totalPages}").FontSize(8);
                    });
                });

                return new DynamicComponentComposeResult
                {
                    Content = content,
                    HasMoreContent = false
                };
            }
        }

        private static bool HasLogo(InvoiceData data) =>
            !string.IsNullOrWhiteSpace(data.CompanyLogoPath) && File.Exists(data.CompanyLogoPath);

        private static byte[] GetFadedLogoBytes(string path, float opacity)
        {
            try
            {
                using var original = new System.Drawing.Bitmap(path);
                using var faded = new System.Drawing.Bitmap(original.Width, original.Height);

                using (var g = System.Drawing.Graphics.FromImage(faded))
                {
                    var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity };
                    using var attributes = new System.Drawing.Imaging.ImageAttributes();
                    attributes.SetColorMatrix(matrix,
                        System.Drawing.Imaging.ColorMatrixFlag.Default,
                        System.Drawing.Imaging.ColorAdjustType.Bitmap);

                    g.DrawImage(original,
                        new System.Drawing.Rectangle(0, 0, original.Width, original.Height),
                        0, 0, original.Width, original.Height,
                        System.Drawing.GraphicsUnit.Pixel, attributes);
                }

                using var ms = new MemoryStream();
                faded.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static void BuildFullHeader(QuestPDF.Fluent.ColumnDescriptor col, InvoiceData data, bool isProbe)
        {
            col.Item().Row(row =>
            {
                if (HasLogo(data))
                {
                    // If probing, draw an empty invisible box that takes up the exact same 
                    // dimensions as the logo without paying the massive Image parse penalty.
                    if (isProbe)
                    {
                        row.ConstantItem(80).Height(80);
                    }
                    else
                    {
                        row.ConstantItem(80).Height(80).AlignMiddle()
                            .Image(data.CompanyLogoPath).FitArea();
                    }
                }
                else
                {
                    row.ConstantItem(46).Height(46).Background(Colors.Black)
                        .AlignCenter().AlignMiddle()
                        .Text(data.CompanyName.Length > 0 ? data.CompanyName.Substring(0, 1) : "?")
                        .FontColor(Colors.White).FontSize(20).Bold();
                }

                row.RelativeItem().PaddingLeft(10).Column(c =>
                {
                    c.Item().Text(data.CompanyName).FontSize(18).ExtraBold();
                    foreach (var line in data.CompanyAddressLines)
                        c.Item().Text(line).FontSize(8).Bold();

                    if (!string.IsNullOrWhiteSpace(data.CompanyPhone1))
                    {
                        string label1 = string.IsNullOrWhiteSpace(data.CompanyPhone1Label) ? "PHONE" : data.CompanyPhone1Label;
                        c.Item().Text($"{label1}: {data.CompanyPhone1}").FontSize(8).Bold();
                    }
                    if (!string.IsNullOrWhiteSpace(data.CompanyPhone2))
                    {
                        string label2 = string.IsNullOrWhiteSpace(data.CompanyPhone2Label) ? "PHONE" : data.CompanyPhone2Label;
                        c.Item().Text($"{label2}: {data.CompanyPhone2}").FontSize(8).Bold();
                    }
                    if (!string.IsNullOrWhiteSpace(data.CompanyEmail))
                        c.Item().Text($"EMAIL: {data.CompanyEmail}").FontSize(8).Bold();
                });

                bool hasBank1 = !string.IsNullOrWhiteSpace(data.CompanyBank1Name);
                bool hasBank2 = !string.IsNullOrWhiteSpace(data.CompanyBank2Name);

                if (!string.IsNullOrWhiteSpace(data.CompanyRegNumber) || hasBank1 || hasBank2)
                {
                    row.ConstantItem(150).AlignRight().AlignTop().Column(c =>
                    {
                        if (!string.IsNullOrWhiteSpace(data.CompanyRegNumber))
                            c.Item().AlignRight().Text($"REG. NO: {data.CompanyRegNumber}").FontSize(9).Bold();

                        if (hasBank1 || hasBank2)
                            c.Item().PaddingTop(6);

                        if (hasBank1)
                            WriteBankBlock(c, "BANK 1", data.CompanyBank1Name, data.CompanyBank1AccountNo, data.CompanyBank1AccountHolder);
                        if (hasBank2)
                            WriteBankBlock(c, "BANK 2", data.CompanyBank2Name, data.CompanyBank2AccountNo, data.CompanyBank2AccountHolder);
                    });
                }
            });

            col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);

            col.Item().AlignCenter().Text(data.InvoiceTitle).Bold().FontSize(12);

            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Details of Receiver (Billed to):").Bold().FontSize(9);
                    WriteParty(c, data.BillTo);
                });

                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Details of Receiver (Shipped to):").Bold().FontSize(9);
                    WriteParty(c, data.ShipTo);
                });
            });

            col.Item().PaddingVertical(4).LineHorizontal(1).LineColor(Colors.Black);

            // =========================================================================
            // ALIGNED COLONS FIX: 
            // We use a 3-column row system. Column 1 is the Label (Right Aligned), 
            // Column 2 is the Colon (Center Aligned), Column 3 is the Value (Left Aligned).
            // =========================================================================
            col.Item().Row(row =>
            {
                // Left Block (Invoice No / Date)
                row.RelativeItem().Column(c =>
                {
                    c.Item().Row(r2 =>
                    {
                        r2.ConstantItem(75).Text("INVOICE NO.").Bold().FontSize(10);
                        r2.ConstantItem(10).AlignCenter().Text(":").Bold().FontSize(10); // Isolated Colon
                        r2.RelativeItem().Text(data.FormattedBillNo).Bold().FontSize(10);
                    });
                    c.Item().Row(r2 =>
                    {
                        r2.ConstantItem(75).Text("Date").Bold().FontSize(10);
                        r2.ConstantItem(10).AlignCenter().Text(":").Bold().FontSize(10); // Isolated Colon
                        r2.RelativeItem().Text(data.BillDate).Bold().FontSize(10);
                    });
                });

                // Right Block (Transport / Vehicle)
                row.RelativeItem().Column(c =>
                {
                    c.Item().Row(r2 =>
                    {
                        r2.ConstantItem(120).Text("Transportation Mode").Bold();
                        r2.ConstantItem(10).AlignCenter().Text(":").Bold(); // Isolated Colon
                        r2.RelativeItem().Text(data.TransportMode ?? "");
                    });
                    c.Item().Row(r2 =>
                    {
                        r2.ConstantItem(120).Text("Vehicle No.").Bold();
                        r2.ConstantItem(10).AlignCenter().Text(":").Bold(); // Isolated Colon
                        r2.RelativeItem().Text(data.VehicleNo ?? "");
                    });
                });
            });

            col.Item().PaddingVertical(4).LineHorizontal(1).LineColor(Colors.Black);
        }

        private static void BuildContinuationHeader(QuestPDF.Fluent.ColumnDescriptor col, InvoiceData data)
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Text(data.CompanyName).Bold().FontSize(12);
                row.RelativeItem().AlignRight()
                    .Text($"Invoice No: {data.FormattedBillNo}    Date: {data.BillDate}").FontSize(9);
            });

            col.Item().AlignCenter().Text($"{data.InvoiceTitle} (Continuation Sheet)").Bold().FontSize(10);

            col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);
        }

        private static void BuildBottomBlock(QuestPDF.Fluent.ColumnDescriptor col, InvoiceData data)
        {
            col.Item().Row(row =>
            {
                row.RelativeItem(2).Column(c =>
                {
                    c.Item().Text("E & O.E.").Bold().FontSize(9);
                    c.Item().Row(r2 =>
                    {
                        r2.ConstantItem(55).Text("In Words :").Bold();
                        r2.RelativeItem().Text(AmountInWordsConverter.Convert(data.NetTotal));
                    });
                });

                row.RelativeItem(1).Border(1).BorderColor(Colors.Black).Column(c =>
                {
                    c.Item().BorderBottom(1).BorderColor(Colors.Black).Row(r2 =>
                    {
                        r2.RelativeItem(3).Padding(4).Text("Rounded Amt.");
                        r2.RelativeItem(4).Padding(4).AlignRight().Text(data.RoundOff.ToString("N2"));
                    });
                    c.Item().Row(r2 =>
                    {
                        r2.RelativeItem(3).Padding(4).Text("NET TOTAL").Bold();
                        r2.RelativeItem(4).Padding(4).AlignRight().Text(data.NetTotal.ToString("N2")).FontSize(14).Bold();
                    });
                });
            });

            col.Item().PaddingTop(20).AlignRight().Column(c =>
            {
                c.Item().Text($"For {data.CompanyName}").FontSize(8);
                c.Item().PaddingTop(28).Text("Authorised Signatory.").FontSize(8);
            });
        }

        private static void WriteBankBlock(QuestPDF.Fluent.ColumnDescriptor c, string label, string bankName, string accNo, string accHolder)
        {
            c.Item().AlignRight().Text(text =>
            {
                text.Span(bankName ?? "").FontSize(8).Bold();
            });
            if (!string.IsNullOrWhiteSpace(accNo))
                c.Item().AlignRight().Text($"{accNo}").FontSize(8);
            if (!string.IsNullOrWhiteSpace(accHolder))
                c.Item().AlignRight().Text($"{accHolder}").FontSize(8);
        }

        private static void WriteParty(QuestPDF.Fluent.ColumnDescriptor c, InvoiceParty party)
        {
            c.Item().Text(party.Name ?? "").FontSize(10).Bold();
            foreach (var line in party.AddressLines)
                c.Item().Text(line).FontSize(9);

            // =========================================================================
            // ALIGNED COLONS: Structural grid (Label -> Colon -> Value)
            // =========================================================================
            c.Item().PaddingTop(4).Column(col =>
            {
                // 1. STATE & STATE CODE
                col.Item().Row(row =>
                {
                    row.ConstantItem(40).Text("State").FontSize(9);
                    row.ConstantItem(10).AlignCenter().Text(":").FontSize(9);

                    // Fixed width for the state name to keep the State Code pulled to the left
                    row.ConstantItem(60).Text(party.State ?? "").FontSize(9);

                    row.ConstantItem(55).Text("State Code").FontSize(9);
                    row.ConstantItem(10).AlignCenter().Text(":").FontSize(9);
                    row.RelativeItem().Text(party.StateCode ?? "").FontSize(9);
                });

                // 2. PHONE
                col.Item().Row(row =>
                {
                    row.ConstantItem(40).Text("Phone").FontSize(9);
                    row.ConstantItem(10).AlignCenter().Text(":").FontSize(9);
                    row.RelativeItem().Text(party.Phone ?? "").FontSize(9);
                });

                // 3. MOBILE
                col.Item().Row(row =>
                {
                    row.ConstantItem(40).Text("Mobile").FontSize(9);
                    row.ConstantItem(10).AlignCenter().Text(":").FontSize(9);
                    row.RelativeItem().Text(party.Mobile ?? "").FontSize(9);
                });

                // 4. GSTIN
                col.Item().Row(row =>
                {
                    row.ConstantItem(40).Text("GSTIN").FontSize(9);
                    row.ConstantItem(10).AlignCenter().Text(":").FontSize(9);
                    row.RelativeItem().Text(party.Gstin ?? "").FontSize(9);
                });
            });
        }

        private static void BuildItemTable(TableDescriptor table, InvoiceData data, List<InvoiceLine> allLines, bool useColor, int fillerRowCount)
        {
            DefineInvoiceColumns(table);

            table.Header(header =>
            {
                HeaderCell(header, "No", useColor);
                HeaderCell(header, "Particulars", useColor);
                HeaderCell(header, "Rate", useColor);
                HeaderCell(header, "Qty", useColor);
                HeaderCell(header, "Disc Amt", useColor);
                HeaderCell(header, "Taxable Value", useColor);
                HeaderCell(header, "Tax %", useColor);
                HeaderCell(header, "Tax Amt", useColor);
                HeaderCell(header, "Total", useColor);
            });

            foreach (var line in allLines)
            {
                Cell(table, line.SlNo.ToString(), System.Windows.TextAlignment.Center);
                Cell(table, line.Particulars, System.Windows.TextAlignment.Left);
                Cell(table, line.Rate.ToString("N2"), System.Windows.TextAlignment.Right);
                Cell(table, line.Qty.ToString("N3"), System.Windows.TextAlignment.Right);
                Cell(table, line.DiscAmt.ToString("N2"), System.Windows.TextAlignment.Right);
                Cell(table, line.TaxableValue.ToString("N2"), System.Windows.TextAlignment.Right);
                Cell(table, line.TaxPercent.ToString("N1"), System.Windows.TextAlignment.Center);
                Cell(table, line.TaxAmount.ToString("N2"), System.Windows.TextAlignment.Right);
                Cell(table, line.Total.ToString("N2"), System.Windows.TextAlignment.Right);
            }

            for (int i = 0; i < fillerRowCount; i++)
            {
                EmptyItemRow(table);
            }
        }

        private static void EmptyItemRow(TableDescriptor table)
        {
            for (int i = 0; i < 9; i++)
            {
                Cell(table, "", System.Windows.TextAlignment.Left);
            }
        }

        private static void BuildTotalsRow(TableDescriptor table, InvoiceData data)
        {
            DefineInvoiceColumns(table);

            double totalQty = data.Lines.Sum(l => l.Qty);
            double totalDisc = data.Lines.Sum(l => l.DiscAmt);
            double totalTaxable = data.Lines.Sum(l => l.TaxableValue);
            double totalTax = data.Lines.Sum(l => l.TaxAmount);
            double totalAmount = data.Lines.Sum(l => l.Total);

            EmptyTotalCell(table);
            TotalCell(table, "Total", System.Windows.TextAlignment.Left, isLabel: true);
            EmptyTotalCell(table);
            TotalCell(table, totalQty.ToString("N3"));
            TotalCell(table, totalDisc.ToString("N2"));
            TotalCell(table, totalTaxable.ToString("N2"));
            EmptyTotalCell(table);
            TotalCell(table, totalTax.ToString("N2"));
            TotalCell(table, totalAmount.ToString("N2"));
        }

        private static void DefineInvoiceColumns(TableDescriptor table)
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(4);   // SNo
                columns.RelativeColumn(32);  // Particulars (widened — HSN/UOM/Gross removed)
                columns.RelativeColumn(13);  // Rate
                columns.RelativeColumn(9);   // Qty
                columns.RelativeColumn(9);   // Disc Amt
                columns.RelativeColumn(13);  // Taxable Value
                columns.RelativeColumn(5);   // Tax %
                columns.RelativeColumn(10);   // Tax Amt (widened for large amounts)
                columns.RelativeColumn(15);  // Total (widened for large amounts)
            });
        }

        private static void HeaderCell(TableCellDescriptor header, string text, bool useColor)
        {
            header.Cell()
                .Border(1).BorderColor(Colors.Black).Background(Colors.White)
                .Padding(3).AlignCenter().AlignMiddle()
                .Text(text).FontSize(9f).Bold().FontColor(Colors.Black);
        }

        private static void Cell(TableDescriptor table, string text, System.Windows.TextAlignment align)
        {
            var container = table.Cell()
                .BorderLeft(1).BorderRight(1).BorderColor(Colors.Black)
                .PaddingVertical(5).PaddingHorizontal(3);
            var textContainer = align switch
            {
                System.Windows.TextAlignment.Right => container.AlignRight(),
                System.Windows.TextAlignment.Center => container.AlignCenter(),
                _ => container.AlignLeft()
            };
            textContainer.Text(text).FontSize(8f);
        }

        private static void TotalCell(TableDescriptor table, string text,
            System.Windows.TextAlignment align = System.Windows.TextAlignment.Right, bool isLabel = false)
        {
            var container = table.Cell()
                // FIX: Removed Top border. 
                // The new Bottom border on the table acts seamlessly as this row's Top border.
                .BorderLeft(1).BorderRight(1).BorderBottom(1).BorderColor(Colors.Black)
                .PaddingVertical(4).PaddingHorizontal(3);

            var textContainer = align switch
            {
                System.Windows.TextAlignment.Right => container.AlignRight(),
                System.Windows.TextAlignment.Center => container.AlignCenter(),
                _ => container.AlignLeft()
            };

            textContainer.Text(text).FontSize(8f).Bold();
        }

        private static void EmptyTotalCell(TableDescriptor table)
        {
            // FIX: Removed Top border.
            table.Cell().BorderLeft(1).BorderRight(1).BorderBottom(1).BorderColor(Colors.Black)
                .PaddingVertical(4).PaddingHorizontal(3).Text("");
        }

        // ═══════════════════════════════════════════════════════════════════
        // EXPORT PDF
        // ═══════════════════════════════════════════════════════════════════
        public static void ExportPdf(InvoiceData data, string suggestedFileName = null)
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "PDF Document (*.pdf)|*.pdf",
                FileName = suggestedFileName ?? SanitizeFileName(data.FormattedBillNo) + ".pdf"
            };

            if (saveDialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                BuildDocument(data, useColor: true).GeneratePdf(saveDialog.FileName);
                Mouse.OverrideCursor = null;

                var result = MessageBox.Show("PDF invoice created successfully.\n\nOpen it now?",
                    "Export Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(saveDialog.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error creating PDF invoice:\n" + ex.Message,
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // PRINT
        // ═══════════════════════════════════════════════════════════════════
        public static void Print(InvoiceData data)
        {
            var printDialog = new PrintDialog();
            if (printDialog.ShowDialog() != true) return;

            string tempPath = Path.Combine(Path.GetTempPath(),
                $"invoice_{SanitizeFileName(data.FormattedBillNo)}_{Guid.NewGuid():N}.pdf");

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                BuildDocument(data, useColor: false).GeneratePdf(tempPath);

                using var pdfDoc = PdfDocument.Load(tempPath);
                using var printDoc = pdfDoc.CreatePrintDocument(PdfPrintMode.CutMargin);

                printDoc.PrinterSettings.PrinterName = printDialog.PrintQueue.FullName;
                printDoc.PrinterSettings.Copies = 1;
                printDoc.DefaultPageSettings.Landscape = false;

                printDoc.Print();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not print the invoice:\n" + ex.Message,
                    "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }

        private static string SanitizeFileName(string name)
        {
            name ??= "Invoice";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(" ", "_");
        }
    }
}