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
    // Purchase Return Cash / Bank / Credit Settlement Report — one row per
    // return entry, showing how the refund was actually settled with the
    // vendor. Mirrors PurchaseSettlementReportView/SalesReturnSettlementReportView;
    // reuses GreaterThanZeroConverter (declared in PurchaseSettlementReportView.xaml.cs,
    // same BillPro.Views namespace) for the credit-row highlighting.
    public partial class PurchaseReturnSettlementReportView : UserControl
    {
        private readonly PurchaseReturnRepository _returnRepo;
        private ObservableCollection<PurchaseReturnSettlementReportRow> _rows = new();

        private bool _initialized;

        public PurchaseReturnSettlementReportView()
        {
            InitializeComponent();

            _returnRepo = new PurchaseReturnRepository();
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

        private void TxtVendorSearch_KeyDown(object sender, KeyEventArgs e)
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
            if (sfReport.SelectedItem is PurchaseReturnSettlementReportRow row)
                OpenEditForEntry(row.EntryNo);
        }

        private void MenuEditBill_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is PurchaseReturnSettlementReportRow row)
                OpenEditForEntry(row.EntryNo);
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is not PurchaseReturnSettlementReportRow row) return;

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

                var detailWindow = new PurchaseReturnDetailWindow(summary, items)
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
            if (sfReport.SelectedItem is PurchaseReturnSettlementReportRow row && !string.IsNullOrWhiteSpace(row.FormattedEntryNo))
                Clipboard.SetText(row.FormattedEntryNo);
        }

        private void OpenEditForEntry(int entryNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Purchase Return Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.PurchaseReturnEntry, entryNo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "PURCHASE RETURN CASH / BANK / CREDIT SETTLEMENT REPORT";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Entry Date", nameof(PurchaseReturnSettlementReportRow.EntryDate),        width: 85),
                new ReportColumn("Entry No",   nameof(PurchaseReturnSettlementReportRow.FormattedEntryNo), width: 95),
                new ReportColumn("Supplier",   nameof(PurchaseReturnSettlementReportRow.VendorName),       width: 190),
                new ReportColumn("Net Total",  nameof(PurchaseReturnSettlementReportRow.NetTotal),         width: 100, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Cash",       nameof(PurchaseReturnSettlementReportRow.ToCash),           width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank",       nameof(PurchaseReturnSettlementReportRow.ToBank),           width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank Name",  nameof(PurchaseReturnSettlementReportRow.BankHeadName),     width: 120),
                new ReportColumn("Credit",     nameof(PurchaseReturnSettlementReportRow.ToCredit),         width: 90,  format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>
            {
                $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}"
            };

            if (!string.IsNullOrWhiteSpace(txtVendorSearch.Text))
                parts.Add($"Search: \"{txtVendorSearch.Text}\"");

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

                var settlements = _returnRepo.GetSettlementReport(fromDate, toDate, txtVendorSearch.Text);

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