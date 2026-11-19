using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Cash / Bank / Credit Settlement Report — one row per bill, showing how
    // the bill's net total was actually settled, so the client can see at a
    // glance how much of a period's purchases are still owed to vendors.
    public partial class PurchaseSettlementReportView : UserControl
    {
        private readonly PurchaseRepository _purchaseRepo;
        private ObservableCollection<PurchaseSettlementReportRow> _rows = new();

        private bool _initialized;

        public PurchaseSettlementReportView()
        {
            InitializeComponent();

            _purchaseRepo = new PurchaseRepository();
            _purchaseRepo.InitializeDatabase();

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
        // ROW ACTIONS — same drill-down pattern as PurchaseReportView
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is PurchaseSettlementReportRow row)
                OpenEditForBill(row.PurchaseNo);
        }

        private void MenuEditBill_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is PurchaseSettlementReportRow row)
                OpenEditForBill(row.PurchaseNo);
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is not PurchaseSettlementReportRow row) return;

            try
            {
                var summary = _purchaseRepo.GetSummaryByNo(row.PurchaseNo);
                if (summary == null)
                {
                    MessageBox.Show("This bill could not be found (it may have been deleted).",
                        "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var items = _purchaseRepo.GetItemsByPurchaseNo(row.PurchaseNo);
                var expenses = _purchaseRepo.GetExpensesByPurchaseNo(row.PurchaseNo);
                _purchaseRepo.ResolveAcHeadNames(expenses);

                var detailWindow = new PurchaseDetailWindow(summary, items, expenses)
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
            if (sfReport.SelectedItem is PurchaseSettlementReportRow row && !string.IsNullOrWhiteSpace(row.BillNo))
                Clipboard.SetText(row.BillNo);
        }

        private void OpenEditForBill(int purchaseNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Purchase Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.PurchaseEntry, purchaseNo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "CASH / BANK / CREDIT SETTLEMENT REPORT";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Pur Date",   nameof(PurchaseSettlementReportRow.PurchaseDate), width: 85),
                new ReportColumn("Bill No",    nameof(PurchaseSettlementReportRow.BillNo),       width: 95),
                new ReportColumn("Supplier",   nameof(PurchaseSettlementReportRow.VendorName),   width: 190),
                new ReportColumn("Net Total",  nameof(PurchaseSettlementReportRow.NetTotal),     width: 100, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Cash",       nameof(PurchaseSettlementReportRow.ToCash),       width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank",       nameof(PurchaseSettlementReportRow.ToBank),       width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Bank Name",  nameof(PurchaseSettlementReportRow.BankHeadName), width: 120),
                new ReportColumn("Credit",     nameof(PurchaseSettlementReportRow.ToCredit),     width: 90,  format: "N2", alignment: TextAlignment.Right),
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

                var settlements = _purchaseRepo.GetSettlementReport(fromDate, toDate, txtVendorSearch.Text);

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

    // Tiny local converter for the credit-row highlighting DataTrigger in the
    // XAML — true when the bound decimal amount is greater than zero.
    public class GreaterThanZeroConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal d) return d > 0;
            if (value is double db) return db > 0;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}