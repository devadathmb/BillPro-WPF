using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // ═══════════════════════════════════════════════════════════════════════
    // REUSABLE REPORT PATTERN
    // Each report view: one ObservableCollection bound to the grid, a
    // Display() that re-runs the aggregating repository query using the
    // current filter panel values, and a Print stub for later.
    // Copy this class for future reports — only LoadReport() and the filter
    // fields need to change.
    // ═══════════════════════════════════════════════════════════════════════
    public partial class OpeningStockReportView : UserControl
    {
        private readonly OpeningStockRepository _repo;
        private ObservableCollection<OpeningStockReportRow> _rows = new();

        public OpeningStockReportView()
        {
            InitializeComponent();

            _repo = new OpeningStockRepository();
            _repo.InitializeDatabase();

            sfReport.ItemsSource = _rows;

            // ── FINANCIAL YEAR FILTER (not implemented yet) ──────────────────
            // Once Financial Year is available, initialise From/To here from
            // the current FY start date instead of showing everything since
            // inception. See the matching commented block in the XAML and in
            // LoadReport() below.
            //
            // dpFromDate.SelectedDate = _financialYearRepo.GetCurrentFyStartDate();
            // dpToDate.SelectedDate = DateTime.Today;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(LoadReport));
        }

        // ════════════════════════════════════════════════════════════════════
        // FILTER PANEL HANDLERS
        // ════════════════════════════════════════════════════════════════════

        // ── FINANCIAL YEAR FILTER (not implemented yet) ──────────────────────
        // Re-enable once the date-range controls are back in the XAML.
        //
        // private void ChkShowAll_CheckedChanged(object sender, RoutedEventArgs e)
        // {
        //     bool showAll = chkShowAll.IsChecked == true;
        //     dpFromDate.IsEnabled = !showAll;
        //     dpToDate.IsEnabled = !showAll;
        // }

        private void TxtItemSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                LoadReport();
                e.Handled = true;
            }
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print and Excel
        // Defined once so both outputs always match the on-screen grid and
        // each other. Copy this method (and the two button handlers below)
        // for future reports — nothing in the helpers themselves changes.
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "OPENING STOCK REPORT ";

        private List<ReportColumn> BuildReportColumns()
        {
            // Mirrors the grid columns in the XAML 1:1 — keep the two in sync
            // when the grid changes.
            return new List<ReportColumn>
            {
                new ReportColumn("Code",           nameof(OpeningStockReportRow.ItemCode),     width: 70),
                new ReportColumn("Item Name",      nameof(OpeningStockReportRow.ItemName),     width: 220),
                new ReportColumn("MRP",            nameof(OpeningStockReportRow.Mrp),          width: 80,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Sales Rate",     nameof(OpeningStockReportRow.SalesRate),    width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Purchase Rate",  nameof(OpeningStockReportRow.PurchaseRate), width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Opening Qty",    nameof(OpeningStockReportRow.TotalQty),     width: 90,  format: "N3", alignment: TextAlignment.Right),
                new ReportColumn("Amount",         nameof(OpeningStockReportRow.Amount),       width: 100, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle() =>
            string.IsNullOrWhiteSpace(txtItemSearch.Text) ? null : $"Item Search: \"{txtItemSearch.Text}\"";

        private string[] BuildFooterLines() => new[]
        {
            $"Total Qty: {_rows.Sum(r => r.TotalQty):N3}",
            $"Total Stock Value: {_rows.Sum(r => r.Amount):N2}"
        };

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("There is nothing to print. Load the report first.",
                    "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // v2: draws the report by hand (see ReportPrintHelper) instead of
            // printing the grid directly — reliable color/B&W and borderless
            // layout with just two separator lines, regardless of what
            // Syncfusion's own print pipeline does.
            ReportPrintHelper.Print(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _rows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                pageMargin: new Thickness(40, 60, 28, 28),// Left, Top, Right, Bottom
                baseFontSize: 11.0 // Increases font size (default is 9.0)
            );

        }

        private void BtnExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Load the report first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportExcelHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _rows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle());
        }

        private void BtnPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Load the report first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Always color, with the same alternating row shading as the Excel
            // export. Print (above) is always black & white for the physical
            // printer — this is the colored, save-to-file counterpart.
            ReportPdfHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _rows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle());
        }

        // ════════════════════════════════════════════════════════════════════
        // LOAD REPORT — the only method that changes between reports
        // ════════════════════════════════════════════════════════════════════
        private void LoadReport()
        {
            try
            {
                // Shows every opening-stock movement since inception for now.
                // Once Financial Year exists, pass its start date through here
                // (see commented fromDate/toDate params on the repository method).
                var rows = _repo.GetOpeningStockReport(itemSearch: txtItemSearch.Text);

                _rows.Clear();
                foreach (var row in rows)
                    _rows.Add(row);

                // Totals now live in a plain footer panel below the grid
                // (see the XAML), not a GridTableSummaryRow — same numbers,
                // just drawn free-form like ReportPdfHelper's footer instead
                // of being squeezed into the grid's row-height system.
                lblGrandTotalQty.Text = $"Total Qty: {_rows.Sum(r => r.TotalQty):N3}";
                lblGrandTotalAmount.Text = $"Total Stock Value: {_rows.Sum(r => r.Amount):N2}";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}