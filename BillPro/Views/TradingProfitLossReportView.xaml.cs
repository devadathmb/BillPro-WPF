using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
    // Trading and Profit & Loss A/c — every ac_head OUTSIDE the Balance
    // Sheet group (ac_group_id <> 0), posted within the selected From/To
    // range, split Expenses / Income side by side, with Net Profit/Net
    // Loss for the period folded in so the two totals balance. One row
    // per pair of (Expense item, Income item) — same "two lists zipped
    // together" shape as BalanceSheetReportView — sourced via
    // TaxLedgerRepository.GetTradingProfitLoss.
    public partial class TradingProfitLossReportView : UserControl
    {
        private readonly TaxLedgerRepository _taxLedgerRepo;
        private ObservableCollection<ProfitLossRow> _rows = new();

        // sfExpense and sfIncome both bind to the same _rows collection —
        // each row already carries both sides' data, same pattern as
        // BalanceSheetReportView's sfLiability/sfAsset.

        private bool _initialized;

        public TradingProfitLossReportView()
        {
            InitializeComponent();

            _taxLedgerRepo = new TaxLedgerRepository();

            sfExpense.ItemsSource = _rows;
            sfIncome.ItemsSource = _rows;

            var today = DateTime.Today;
            int fyStartYear = today.Month >= 4 ? today.Year : today.Year - 1;
            dpFromDate.SelectedDate = new DateTime(fyStartYear, 4, 1);
            dpToDate.SelectedDate = today;

            // Double-click a row on either side to see that a/c head's
            // full history on the Ledger Report, over the same From/To
            // range shown here. Separate handlers per grid because
            // sfExpense/sfIncome share one ProfitLossRow per row but each
            // side names a completely different a/c head — see
            // EntryDrillDownHelper's class doc comment for the broader
            // convention this follows.
            sfExpense.MouseDoubleClick += SfExpense_MouseDoubleClick;
            sfIncome.MouseDoubleClick += SfIncome_MouseDoubleClick;

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(LoadReport));
        }

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS — jump to the Ledger Report. ProfitLossRow only
        // carries a display Particulars string per side (not an
        // ac_head_code — see TaxLedgerModels.cs), so the name is passed
        // through and matched on the Ledger Report side instead; this also
        // means synthetic lines like "To Net Profit"/"By Net Loss"/"Gross
        // Profit c/d" harmlessly no-op there since they don't match any
        // real a/c head. The Total/Gross-Profit/Balancing rows and a side
        // with no item for this row (null Particulars) are all no-ops.
        // ════════════════════════════════════════════════════════════════════
        private void SfExpense_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfExpense.SelectedItem is not ProfitLossRow row) return;
            if (row.IsTotalRow || row.IsGrossProfitRow || row.IsBalancingRow) return;
            if (string.IsNullOrWhiteSpace(row.ExpenseParticulars)) return;

            OpenLedgerReport(row.ExpenseParticulars);
        }

        private void SfIncome_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfIncome.SelectedItem is not ProfitLossRow row) return;
            if (row.IsTotalRow || row.IsGrossProfitRow || row.IsBalancingRow) return;
            if (string.IsNullOrWhiteSpace(row.IncomeParticulars)) return;

            OpenLedgerReport(row.IncomeParticulars);
        }

        private void OpenLedgerReport(string acHeadName)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Ledger (Individual Heads)");
            EntryEditNavigator.RequestEdit(EntryChannels.LedgerReport,
                (AcHeadCode: (string)null, AcHeadName: acHeadName,
                 FromDate: dpFromDate.SelectedDate ?? DateTime.Today,
                 ToDate: dpToDate.SelectedDate ?? DateTime.Today));
        }

        // ════════════════════════════════════════════════════════════════════
        // FILTER PANEL
        // ════════════════════════════════════════════════════════════════════
        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized) return;
            LoadReport();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "TRADING AND PROFIT & LOSS A/C";

        private List<ReportColumn> BuildReportColumns()
        {
            // Headers are "Particulars"/"Amount" on both sides now — the
            // "Expenses"/"Income" labels moved up a tier into the group
            // header (see BuildColumnGroups), matching the big EXPENSES/
            // INCOME headings already shown above each SfDataGrid in the
            // form, with Particulars/Amount as the sub-header underneath.
            return new List<ReportColumn>
            {
                new ReportColumn("Particulars",  nameof(ProfitLossRow.ExpenseParticulars), width: 220),
                new ReportColumn("Amount",       nameof(ProfitLossRow.ExpenseAmount),       width: 120, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Particulars",  nameof(ProfitLossRow.IncomeParticulars),   width: 220),
                new ReportColumn("Amount",       nameof(ProfitLossRow.IncomeAmount),         width: 120, format: "N2", alignment: TextAlignment.Right),
            };
        }

        // Two groups of two columns each — EXPENSES covers the Expense
        // Particulars/Amount pair, INCOME covers the Income pair. Also
        // gets a vertical divider drawn between them by Print/PDF/Excel,
        // matching the black Border already separating sfExpense/sfIncome
        // on screen.
        private List<ReportColumnGroup> BuildColumnGroups() => new List<ReportColumnGroup>
        {
            new ReportColumnGroup("EXPENSES", 2),
            new ReportColumnGroup("INCOME", 2),
        };

        private string BuildSubTitle() =>
            $"For the period from {dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}";

        // Parses the "N2"-formatted totals labels back to decimal for the
        // difference calculation — same labels already shown in the form,
        // so this can never drift from what's on screen.
        private double GetTotalExpenseValue() =>
            double.TryParse(lblTotalExpense.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double v) ? v : 0;

        private double GetTotalIncomeValue() =>
            double.TryParse(lblTotalIncome.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double v) ? v : 0;

        private string[] BuildFooterLines()
        {
            var lines = new List<string>
            {
                $"Total Expense: {lblTotalExpense.Text}",
                $"Total Income: {lblTotalIncome.Text}"
            };

            // Only included when there IS a difference — an exactly
            // balanced Trading & P&L a/c (the normal case, since Net
            // Profit/Loss is folded in specifically to make both sides
            // tie out) shows no difference line at all, same as the form.
            double difference = GetTotalExpenseValue() - GetTotalIncomeValue();
            if (Math.Abs(difference) >= 0.005)
                lines.Add($"Difference: {difference:N2}");

            return lines.ToArray();
        }

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
                rows: _rows.ToList(),
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                pageMargin: new Thickness(40, 60, 28, 28),
                baseFontSize: 11.0,
                columnGroups: BuildColumnGroups());
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
                rows: _rows.ToList(),
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                columnGroups: BuildColumnGroups());
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
                rows: _rows.ToList(),
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                columnGroups: BuildColumnGroups());
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

                var entries = _taxLedgerRepo.GetTradingProfitLoss(fromDate, toDate);

                _rows.Clear();
                foreach (var r in entries)
                    _rows.Add(r);

                // Read the totals off the dedicated "Total" row rather than
                // re-summing here, so the footer can never drift from what
                // the grid itself displays — same reasoning as
                // BalanceSheetReportView.LoadReport.
                var totalRow = entries.LastOrDefault(r => r.IsTotalRow);
                double totalExpense = totalRow?.ExpenseAmount ?? 0;
                double totalIncome = totalRow?.IncomeAmount ?? 0;
                lblTotalExpense.Text = totalExpense.ToString("N2");
                lblTotalIncome.Text = totalIncome.ToString("N2");

                // Difference textblock — only shown when the two sides
                // genuinely don't tie out (should be the rare case, since
                // Net Profit/Loss is folded in specifically to balance
                // them). Zero difference hides the row entirely, same as
                // BuildFooterLines omits the "Difference" line in that case.
                double difference = totalExpense - totalIncome;
                if (Math.Abs(difference) >= 0.005)
                {
                    lblDifference.Text = difference.ToString("N2");
                    lblDifferencePanel.Visibility = Visibility.Visible;
                }
                else
                {
                    lblDifferencePanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}