using System;
using System.Windows;
using System.Windows.Input;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Microsoft.Win32;
using BillPro.Repositories;
using BillPro.Models;

namespace BillPro.Helpers
{
    // ─────────────────────────────────────────────────────────────────────
    // PDF exporter for Receipt & Payment Entry vouchers. Split out of
    // VoucherPrintHelper (which now owns only the physical-printer path)
    // so the two outputs each own their own layout constants — tuning one
    // for its output medium can never silently break the other.
    // Deliberately simple, single-page, fixed layout — the "1245 / PKB
    // ENTERPRISES / VOUCHER" style shown in the reference mock — rather
    // than the letterhead+watermark treatment InvoicePrintHelper uses for
    // sales invoices. Pulls the company header from CompanyRepository so
    // it stays in sync with whatever's set on the Company Details screen.
    // ─────────────────────────────────────────────────────────────────────
    public static class VoucherPdfHelper
    {
        private static bool _fontResolverRegistered;

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
                    "SegoeUI#b" => "segoeuib.ttf",
                    "SegoeUI#i" => "segoeuiz.ttf",
                    "SegoeUI#bi" => "segoeuiz.ttf",
                    _ => "segoeui.ttf",
                };
                string path = System.IO.Path.Combine(fontsDir, fileName);
                if (!System.IO.File.Exists(path))
                    path = System.IO.Path.Combine(fontsDir, "segoeui.ttf");
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

        private static CompanyDetails GetCompany()
        {
            try { return new CompanyRepository().Get(); }
            catch { return new CompanyDetails(); }
        }

        // ── PDF export ──────────────────────────────────────────────────
        public static void ExportPdf(VoucherPrintData data)
        {
            EnsureFontResolver();

            var saveDialog = new SaveFileDialog
            {
                Filter = "PDF Document (*.pdf)|*.pdf",
                FileName = $"{data.VoucherType}_{data.VoucherNo}_{DateTime.Now:yyyyMMdd_HHmm}"
            };
            if (saveDialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var company = GetCompany();
                var document = new PdfDocument();
                var page = document.AddPage();
                page.Orientation = PdfSharp.PageOrientation.Portrait;
                var gfx = XGraphics.FromPdfPage(page);

                DrawVoucher(gfx, page.Width.Point, company, data);

                document.Save(saveDialog.FileName);
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
                MessageBox.Show("Error creating PDF file:\n" + ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private static void DrawVoucher(XGraphics gfx, double pageWidth, CompanyDetails company, VoucherPrintData data)
        {
            double margin = 45;
            double y = 40;
            double contentWidth = pageWidth - margin * 2;

            var regNoFont = new XFont("Segoe UI", 9);
            var nameFont = new XFont("Segoe UI", 16, XFontStyleEx.Bold);
            var addrFont = new XFont("Segoe UI", 9);
            var labelFont = new XFont("Segoe UI", 10);
            var labelBoldFont = new XFont("Segoe UI", 10, XFontStyleEx.Bold);
            var underlineFont = new XFont("Segoe UI", 12, XFontStyleEx.Bold);

            if (!string.IsNullOrWhiteSpace(company.RegNumber))
            {
                gfx.DrawString(company.RegNumber, regNoFont, XBrushes.Black, new XPoint(margin, y));
            }
            y += 20;

            gfx.DrawString(string.IsNullOrWhiteSpace(company.CompanyName) ? "COMPANY NAME" : company.CompanyName.ToUpper(),
                nameFont, XBrushes.Black, new XRect(margin, y, contentWidth, 22), XStringFormats.TopCenter);
            y += 24;

            if (!string.IsNullOrWhiteSpace(company.Address))
            {
                foreach (var line in company.Address.Split('\n'))
                {
                    gfx.DrawString(line.Trim(), addrFont, XBrushes.Black, new XRect(margin, y, contentWidth, 14), XStringFormats.TopCenter);
                    y += 14;
                }
            }
            if (!string.IsNullOrWhiteSpace(company.Phone1))
            {
                gfx.DrawString("PHONE: " + company.Phone1, addrFont, XBrushes.Black, new XRect(margin, y, contentWidth, 14), XStringFormats.TopCenter);
                y += 14;
            }

            y += 18;

            // Voucher No / VOUCHER / Date line
            gfx.DrawString($"Voucher No.: {data.VoucherNo}", labelFont, XBrushes.Black, new XPoint(margin, y + 12));
            gfx.DrawString(data.VoucherType.ToUpper(), underlineFont, XBrushes.Black, new XRect(margin, y, contentWidth, 18), XStringFormats.TopCenter);
            gfx.DrawString($"Date: {data.VoucherDate:dd/MM/yyyy}", labelFont, XBrushes.Black,
                new XRect(margin, y + 12, contentWidth, 14), XStringFormats.TopRight);
            y += 24;
            var underlineWidth = gfx.MeasureString(data.VoucherType.ToUpper(), underlineFont).Width;
            gfx.DrawLine(XPens.Black, pageWidth / 2 - underlineWidth / 2, y - 4, pageWidth / 2 + underlineWidth / 2, y - 4);
            y += 6;

            DrawDashedLine(gfx, margin, y, pageWidth - margin);
            y += 15;


            gfx.DrawString("Head Of Account :", labelFont, XBrushes.Black, new XPoint(margin, y));
            gfx.DrawString(data.AcHeadName, labelFont, XBrushes.Black, new XPoint(margin + 115, y));
            y += 15;
            if (!string.IsNullOrWhiteSpace(data.Address))
            {
                foreach (var line in data.Address.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    gfx.DrawString(line.Trim(), labelFont, XBrushes.Black, new XPoint(margin + 115, y));
                    y += 15;
                }
            }
            if (!string.IsNullOrWhiteSpace(data.Mobile))
            {
                gfx.DrawString("Mobile: " + data.Mobile, labelFont, XBrushes.Black, new XPoint(margin + 115, y));
                y += 15;
            }
            if (!string.IsNullOrWhiteSpace(data.GstNo))
            {
                gfx.DrawString("GSTIN: " + data.GstNo, labelFont, XBrushes.Black, new XPoint(margin + 115, y));
                y += 15;
            }

            y += 4;
            DrawDashedLine(gfx, margin, y, pageWidth - margin);
            y += 15;

            double colDesc = margin, colAmt = margin + 260, colNarr = margin + 360;
            gfx.DrawString("Description", labelBoldFont, XBrushes.Black, new XPoint(colDesc, y));
            gfx.DrawString("Amount", labelBoldFont, XBrushes.Black, new XPoint(colAmt, y));
            gfx.DrawString("Narration", labelBoldFont, XBrushes.Black, new XPoint(colNarr, y));
            y += 15;
            DrawDashedLine(gfx, margin, y, pageWidth - margin);
            y += 14;

            gfx.DrawString(data.DescriptionLabel, labelFont, XBrushes.Black, new XPoint(colDesc, y));
            gfx.DrawString(data.Amount.ToString("0.00"), labelFont, XBrushes.Black, new XPoint(colAmt, y));
            if (!string.IsNullOrWhiteSpace(data.Narration))
                gfx.DrawString(data.Narration, labelFont, XBrushes.Black, new XPoint(colNarr, y));
            y += 18;

            // Discount sits directly under the amount it's discounting off
            // (matches the reference voucher layout), and Total below is
            // the net figure (Amount − Discount), not the gross amount.
            if (data.Discount != 0)
            {
                gfx.DrawString("Discount", labelFont, XBrushes.Black, new XPoint(colDesc, y));
                gfx.DrawString(data.Discount.ToString("0.00"), labelFont, XBrushes.Black, new XPoint(colAmt, y));
                y += 18;
            }

            if (data.Mode == "Bank")
            {
                gfx.DrawString($"Mode: Bank ({data.BankName})", labelFont, XBrushes.Black, new XPoint(colDesc, y));
                y += 15;
                if (!string.IsNullOrWhiteSpace(data.BankNarration))
                {
                    gfx.DrawString("Bank Narration: " + data.BankNarration, labelFont, XBrushes.Black, new XPoint(colDesc, y));
                    y += 15;
                }
            }

            DrawDashedLine(gfx, margin, y, pageWidth - margin);
            y += 15;

            decimal netTotal = data.Amount - data.Discount;
            gfx.DrawString("Total", labelBoldFont, XBrushes.Black, new XPoint(colDesc + 8, y));
            gfx.DrawString(netTotal.ToString("0.00"), labelBoldFont, XBrushes.Black, new XPoint(colAmt, y));
            y += 15;

            DrawDashedLine(gfx, margin, y, pageWidth - margin);
            y += 12;

            y += 40;
            gfx.DrawString("Authorised Signatory.", labelFont, XBrushes.Black, new XPoint(pageWidth - margin - 140, y));
        }

        private static void DrawDashedLine(XGraphics gfx, double x1, double y, double x2)
        {
            var pen = new XPen(XColors.Black, 0.75) { DashStyle = XDashStyle.Dash };
            gfx.DrawLine(pen, x1, y, x2, y);
        }
    }
}