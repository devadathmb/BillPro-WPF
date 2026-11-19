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
    // Sales Cash / Bank / Credit Settlement Report — one row per bill,
    // showing how the bill's net total was actually settled. Mirrors
    // PurchaseSettlementReportView; reuses that file's GreaterThanZeroConverter
    // (same BillPro.Views namespace) for the credit-row highlighting.
    public partial class SalesSettlementReportView : UserControl
    {
        private readonly SalesRepository _salesRepo;
        private readonly SalesSeriesRepository _seriesRepo;
        private ObservableCollection<SalesSettlementReportRow> _rows = new();

        private bool _initialized;

        public SalesSettlementReportView()
        {
            InitializeComponent();

            _salesRepo = new SalesRepository();
            _salesRepo.InitializeDatabase();
            _seriesRepo = new SalesSeriesRepository();
            _seriesRepo.InitializeDatabase();

            sfReport.ItemsSource = _rows;
            seriesFilter.LoadSeries(_seriesRepo.GetActive());

            var today = DateTime.Today;
            dpFromDate.SelectedDate = new DateTime(today.Year, today.Month, 1);
            dpToDate.SelectedDate = today;

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(LoadReport));
        }

        // ════════════════════════════════════════════════════════════════════
        // FILTER PANEL
        // ════════════════════════════════════════════════════════════════════
        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized) return;
            LoadReport();
        }

        private void TxtCustomerSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                LoadReport();
                e.Handled = true;
            }
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is SalesSettlementReportRow row)
                OpenEditForBill(row.Prefix, row.BillNo);
        }

        private void MenuEditBill_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is SalesSettlementReportRow row)
                OpenEditForBill(row.Prefix, row.BillNo);
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is not SalesSettlementReportRow row) return;

            try
            {
                var summary = _salesRepo.GetSummary(row.Prefix, row.BillNo);
                if (summary == null)
                {
                    MessageBox.Show("This bill could not be found (it may have been deleted).",
                        "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var items = _salesRepo.GetItems(row.Prefix, row.BillNo);

                var detailWindow = new SalesDetailWindow(summary, items)
                {
                    Owner = Window.GetWindow(this)
                };
                detailWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading bill details:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuCopyBillNo_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is SalesSettlementReportRow row && !string.IsNullOrWhiteSpace(row.FormattedBillNo))
                Clipboard.SetText(row.FormattedBillNo);
        }

        private void OpenEditForBill(string prefix, int billNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Sales Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.SalesEntry, (prefix, billNo));
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "SALES CASH / BANK / CREDIT SETTLEMENT REPORT";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Bill Date",  nameof(SalesSettlementReportRow.BillDate),        width: 85),
                new ReportColumn("Bill No",    nameof(SalesSettlementReportRow.FormattedBillNo), width: 95),
                new ReportColumn("Customer",   nameof(SalesSettlementReportRow.CustomerName),    width: 190),
                new ReportColumn("Net Total",  nameof(SalesSettlementReportRow.NetTotal),        width: 100, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Cash",       nameof(SalesSettlementReportRow.ToCash),          width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank",       nameof(SalesSettlementReportRow.ToBank),          width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank Name",  nameof(SalesSettlementReportRow.BankHeadName),    width: 120),
                new ReportColumn("Credit",     nameof(SalesSettlementReportRow.ToCredit),        width: 90,  format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>
            {
                $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}"
            };

            var series = seriesFilter.SelectedPrefixes;
            if (series.Count > 0)
                parts.Add($"Series: {string.Join(", ", series)}");

            if (!string.IsNullOrWhiteSpace(txtCustomerSearch.Text))
                parts.Add($"Search: \"{txtCustomerSearch.Text}\"");

            return string.Join("  |  ", parts);
        }

        private string[] BuildFooterLines() => new[]
        {
            $"Cash: {_rows.Sum(r => r.ToCash):N2}",
            $"Bank: {_rows.Sum(r => r.ToBank):N2}",
            $"Credit: {_rows.Sum(r => r.ToCredit):N2}",
            $"Net Total: {_rows.Sum(r => r.NetTotal):N2}"
        };

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("There is nothing to print. Load the report first.",
                    "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportPrintHelper.Print(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _rows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                pageMargin: new Thickness(40, 60, 28, 28),
                baseFontSize: 11.0);
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

            ReportPdfHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _rows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle());
        }

        // ════════════════════════════════════════════════════════════════════
        // LOAD REPORT
        // ════════════════════════════════════════════════════════════════════
        private void LoadReport()
        {
            try
            {
                DateTime fromDate = dpFromDate.SelectedDate ?? DateTime.Today;
                DateTime toDate = dpToDate.SelectedDate ?? DateTime.Today;
                var seriesPrefixes = seriesFilter.SelectedPrefixes;

                var settlements = _salesRepo.GetSettlementReport(fromDate, toDate, txtCustomerSearch.Text, seriesPrefixes);

                _rows.Clear();
                foreach (var r in settlements)
                    _rows.Add(r);

                lblTotalCash.Text = _rows.Sum(r => r.ToCash).ToString("N2");
                lblTotalBank.Text = _rows.Sum(r => r.ToBank).ToString("N2");
                lblTotalCredit.Text = _rows.Sum(r => r.ToCredit).ToString("N2");
                lblGrandTotal.Text = _rows.Sum(r => r.NetTotal).ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}