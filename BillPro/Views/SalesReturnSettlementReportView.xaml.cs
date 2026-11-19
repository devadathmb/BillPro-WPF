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
    // Sales Return Cash / Bank / Credit Settlement Report — one row per
    // return entry, showing how the refund was actually settled. Mirrors
    // SalesSettlementReportView/PurchaseSettlementReportView; reuses that
    // last file's GreaterThanZeroConverter (same BillPro.Views namespace) for
    // the credit-row highlighting — no series filter here since return
    // entries have no series/prefix.
    public partial class SalesReturnSettlementReportView : UserControl
    {
        private readonly SalesReturnRepository _returnRepo;
        private ObservableCollection<SalesReturnSettlementReportRow> _rows = new();

        private bool _initialized;

        public SalesReturnSettlementReportView()
        {
            InitializeComponent();

            _returnRepo = new SalesReturnRepository();
            _returnRepo.InitializeDatabase();

            sfReport.ItemsSource = _rows;

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
            if (sfReport.SelectedItem is SalesReturnSettlementReportRow row)
                OpenEditForEntry(row.EntryNo);
        }

        private void MenuEditBill_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is SalesReturnSettlementReportRow row)
                OpenEditForEntry(row.EntryNo);
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is not SalesReturnSettlementReportRow row) return;

            try
            {
                var summary = _returnRepo.GetSummary(row.EntryNo);
                if (summary == null)
                {
                    MessageBox.Show("This return entry could not be found (it may have been deleted).",
                        "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var items = _returnRepo.GetItems(row.EntryNo);

                var detailWindow = new SalesReturnDetailWindow(summary, items)
                {
                    Owner = Window.GetWindow(this)
                };
                detailWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading return details:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuCopyBillNo_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is SalesReturnSettlementReportRow row && !string.IsNullOrWhiteSpace(row.FormattedEntryNo))
                Clipboard.SetText(row.FormattedEntryNo);
        }

        private void OpenEditForEntry(int entryNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Sales Return Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.SalesReturnEntry, entryNo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "SALES RETURN CASH / BANK / CREDIT SETTLEMENT REPORT";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Entry Date", nameof(SalesReturnSettlementReportRow.EntryDate),        width: 85),
                new ReportColumn("Entry No",   nameof(SalesReturnSettlementReportRow.FormattedEntryNo), width: 95),
                new ReportColumn("Customer",   nameof(SalesReturnSettlementReportRow.CustomerName),     width: 190),
                new ReportColumn("Net Total",  nameof(SalesReturnSettlementReportRow.NetTotal),         width: 100, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Cash",       nameof(SalesReturnSettlementReportRow.ToCash),           width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank",       nameof(SalesReturnSettlementReportRow.ToBank),           width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank Name",  nameof(SalesReturnSettlementReportRow.BankHeadName),     width: 120),
                new ReportColumn("Credit",     nameof(SalesReturnSettlementReportRow.ToCredit),         width: 90,  format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>
            {
                $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}"
            };

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

                var settlements = _returnRepo.GetSettlementReport(fromDate, toDate, txtCustomerSearch.Text);

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