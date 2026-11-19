using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Input;
using BillPro.Repositories;
using BillPro.Models;

namespace BillPro.Helpers
{
    // ─────────────────────────────────────────────────────────────────────
    // Physical-printer output for Receipt & Payment Entry vouchers.
    //
    // This used to be built with a FlowDocument + Table, with the dashed
    // separator lines drawn as literal "-----" text runs. That looked fine
    // on whatever printer it was written against, but it doesn't scale:
    // a fixed count of dash characters only spans a fixed pixel width, so
    // on any other printer (different PrintableAreaWidth from the driver/
    // paper/margins) the lines stop partway across the page and the layout
    // reads as too small / not filling the sheet — while ExportPdf (see
    // VoucherPdfHelper) never had this problem because it draws its rules
    // with gfx.DrawLine() from margin to (pageWidth - margin) directly.
    //
    // Fix: draw the print output the same way — a custom DocumentPaginator
    // that renders straight into a DrawingContext with explicit pixel math
    // off the real printDialog.PrintableAreaWidth/Height, same technique
    // ReportPrintHelper already uses successfully for report printing.
    //
    // Every measurement below is VoucherPdfHelper's PDF-point value times
    // 4/3 (Pdf = 72 DIU/in, WPF print = 96 DIU/in, 96/72 = 4/3), so the two
    // outputs are the physically same layout — same margins, same gap
    // above/below every dashed rule, same gap after Total — not just
    // eyeballed to look similar. If you tweak spacing in one helper, apply
    // the same PDF-pt value * 4/3 here to keep them in sync.
    // ─────────────────────────────────────────────────────────────────────
    public static class VoucherPrintHelper
    {
        // 96 DIU/in ÷ 72 pt/in — multiply any VoucherPdfHelper pt value by
        // this to get the matching print DIU value.
        private const double PtToDiu = 96.0 / 72.0;

        private static CompanyDetails GetCompany()
        {
            try { return new CompanyRepository().Get(); }
            catch { return new CompanyDetails(); }
        }

        public static void Print(VoucherPrintData data)
        {
            var printDialog = new System.Windows.Controls.PrintDialog();
            if (printDialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var company = GetCompany();
                var paginator = new VoucherDocumentPaginator(
                    company, data, printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight);

                printDialog.PrintDocument(paginator, $"{data.VoucherType} Voucher {data.VoucherNo}");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private sealed class VoucherDocumentPaginator : DocumentPaginator
        {
            private readonly CompanyDetails _company;
            private readonly VoucherPrintData _data;
            private Size _pageSize;

            public VoucherDocumentPaginator(CompanyDetails company, VoucherPrintData data, double width, double height)
            {
                _company = company;
                _data = data;
                _pageSize = new Size(width, height);
            }

            public override bool IsPageCountValid => true;
            public override int PageCount => 1;
            public override Size PageSize
            {
                get => _pageSize;
                set => _pageSize = value;
            }
            public override IDocumentPaginatorSource Source => null;

            public override DocumentPage GetPage(int pageNumber)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    DrawVoucher(dc, _pageSize.Width, _company, _data);
                }
                var rect = new Rect(0, 0, _pageSize.Width, _pageSize.Height);
                return new DocumentPage(visual, _pageSize, rect, rect);
            }
        }

        // Structurally and numerically mirrors VoucherPdfHelper.DrawVoucher —
        // every y += / offset here is that method's pt value * PtToDiu.
        private static void DrawVoucher(DrawingContext dc, double pageWidth, CompanyDetails company, VoucherPrintData data)
        {
            double margin = 45 * PtToDiu;          // PDF margin = 45pt
            double y = 40 * PtToDiu;                // PDF start y = 40pt
            double contentWidth = pageWidth - margin * 2;

            var culture = CultureInfo.CurrentCulture;
            var regular = new Typeface("Segoe UI");
            var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var black = Brushes.Black;

            // PDF font sizes (pt) * PtToDiu, matching XFont sizes 1:1.
            double regNoSize = 9 * PtToDiu;
            double nameSize = 16 * PtToDiu;
            double addrSize = 9 * PtToDiu;
            double labelSize = 10 * PtToDiu;
            double underlineSize = 12 * PtToDiu;

            FormattedText Text(string s, Typeface tf, double size) =>
                new FormattedText(s ?? "", culture, FlowDirection.LeftToRight, tf, size, black, 1.0);

            void DrawLeft(string s, Typeface tf, double size, double x, double yy)
                => dc.DrawText(Text(s, tf, size), new Point(x, yy));

            void DrawCentered(string s, Typeface tf, double size, double yy)
            {
                var ft = Text(s, tf, size);
                dc.DrawText(ft, new Point(margin + (contentWidth - ft.Width) / 2, yy));
            }

            void DrawRight(string s, Typeface tf, double size, double rightX, double yy)
            {
                var ft = Text(s, tf, size);
                dc.DrawText(ft, new Point(rightX - ft.Width, yy));
            }

            void DashedLine(double yy)
            {
                var pen = new Pen(Brushes.Black, 0.75 * PtToDiu) { DashStyle = DashStyles.Dash };
                dc.DrawLine(pen, new Point(margin, yy), new Point(pageWidth - margin, yy));
            }

            // ── Company header ──────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(company.RegNumber))
                DrawLeft(company.RegNumber, regular, regNoSize, margin, y);
            y += 20 * PtToDiu;

            DrawCentered(string.IsNullOrWhiteSpace(company.CompanyName) ? "COMPANY NAME" : company.CompanyName.ToUpper(),
                bold, nameSize, y);
            y += 24 * PtToDiu;

            if (!string.IsNullOrWhiteSpace(company.Address))
            {
                foreach (var line in company.Address.Split('\n'))
                {
                    DrawCentered(line.Trim(), regular, addrSize, y);
                    y += 14 * PtToDiu;
                }
            }
            if (!string.IsNullOrWhiteSpace(company.Phone1))
            {
                DrawCentered("PHONE: " + company.Phone1, regular, addrSize, y);
                y += 14 * PtToDiu;
            }

            y += 18 * PtToDiu;

            // ── Voucher No / VOUCHER / Date line ────────────────────────
            DrawLeft($"Voucher No.: {data.VoucherNo}", regular, labelSize, margin, y + 12 * PtToDiu);
            DrawCentered(data.VoucherType.ToUpper(), bold, underlineSize, y);
            DrawRight($"Date: {data.VoucherDate:dd/MM/yyyy}", regular, labelSize, pageWidth - margin, y + 12 * PtToDiu);
            y += 24 * PtToDiu;

            var typeText = Text(data.VoucherType.ToUpper(), bold, underlineSize);
            double typeCenterX = margin + contentWidth / 2;
            dc.DrawLine(new Pen(Brushes.Black, 1 * PtToDiu),
                new Point(typeCenterX - typeText.Width / 2, y - 4 * PtToDiu),
                new Point(typeCenterX + typeText.Width / 2, y - 4 * PtToDiu));
            y += 6 * PtToDiu;

            DashedLine(y);
            y += 15 * PtToDiu;

            // ── Head of Account ──────────────────────────────────────────
            DrawLeft("Head Of Account :", regular, labelSize, margin, y);
            DrawLeft(data.AcHeadName, regular, labelSize, margin + 115 * PtToDiu, y);
            y += 15 * PtToDiu;

            if (!string.IsNullOrWhiteSpace(data.Address))
            {
                foreach (var line in data.Address.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    DrawLeft(line.Trim(), regular, labelSize, margin + 115 * PtToDiu, y);
                    y += 15 * PtToDiu;
                }
            }
            if (!string.IsNullOrWhiteSpace(data.Mobile))
            {
                DrawLeft("Mobile: " + data.Mobile, regular, labelSize, margin + 115 * PtToDiu, y);
                y += 15 * PtToDiu;
            }
            if (!string.IsNullOrWhiteSpace(data.GstNo))
            {
                DrawLeft("GSTIN: " + data.GstNo, regular, labelSize, margin + 115 * PtToDiu, y);
                y += 15 * PtToDiu;
            }

            y += 4 * PtToDiu;
            DashedLine(y);
            y += 15 * PtToDiu;

            // ── Description / Amount / Narration columns ────────────────
            // Same fixed pt offsets from margin as the PDF (*PtToDiu), not
            // proportions of contentWidth, so the columns land in the same
            // physical spot on the page as they do in the PDF.
            double colDesc = margin;
            double colAmt = margin + 260 * PtToDiu;
            double colNarr = margin + 360 * PtToDiu;

            DrawLeft("Description", bold, labelSize, colDesc, y);
            DrawLeft("Amount", bold, labelSize, colAmt, y);
            DrawLeft("Narration", bold, labelSize, colNarr, y);
            y += 15 * PtToDiu;
            DashedLine(y);
            y += 14 * PtToDiu;

            DrawLeft(data.DescriptionLabel, regular, labelSize, colDesc, y);
            DrawLeft(data.Amount.ToString("0.00"), regular, labelSize, colAmt, y);
            if (!string.IsNullOrWhiteSpace(data.Narration))
                DrawLeft(data.Narration, regular, labelSize, colNarr, y);
            y += 18 * PtToDiu;

            // Discount sits directly under the amount it's discounting off
            // (matches the reference voucher layout / VoucherPdfHelper),
            // and Total below is the net figure (Amount − Discount).
            if (data.Discount != 0)
            {
                DrawLeft("Discount", regular, labelSize, colDesc, y);
                DrawLeft(data.Discount.ToString("0.00"), regular, labelSize, colAmt, y);
                y += 18 * PtToDiu;
            }

            if (data.Mode == "Bank")
            {
                DrawLeft($"Mode: Bank ({data.BankName})", regular, labelSize, colDesc, y);
                y += 15 * PtToDiu;
                if (!string.IsNullOrWhiteSpace(data.BankNarration))
                {
                    DrawLeft("Bank Narration: " + data.BankNarration, regular, labelSize, colDesc, y);
                    y += 15 * PtToDiu;
                }
            }

            DashedLine(y);
            y += 15 * PtToDiu;

            decimal netTotal = data.Amount - data.Discount;
            DrawLeft("Total", bold, labelSize, colDesc + 8 * PtToDiu, y);
            DrawLeft(netTotal.ToString("0.00"), bold, labelSize, colAmt, y);
            y += 15 * PtToDiu;

            DashedLine(y);
            y += 12 * PtToDiu;

            y += 40 * PtToDiu;
            DrawLeft("Authorised Signatory.", regular, labelSize, pageWidth - margin - 140 * PtToDiu, y);
        }
    }
}