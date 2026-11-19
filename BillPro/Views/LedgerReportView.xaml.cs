using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // ════════════════════════════════════════════════════════════════════════
    // STYLE SELECTOR (must be public and outside the UserControl class)
    // ════════════════════════════════════════════════════════════════════════
    public class LedgerOpeningRowStyleSelector : StyleSelector
    {
        public Style OpeningRowStyle { get; set; }

        public override Style SelectStyle(object item, DependencyObject container)
        {
            if (item is LedgerTransactionReportRow row && row.IsOpeningBalance)
                return OpeningRowStyle;

            return base.SelectStyle(item, container);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // MAIN VIEW CLASS
    // ════════════════════════════════════════════════════════════════════════
    // Ledger Report — pick any single account head (Cash, a bank, a
    // customer/vendor, a tax head, anything in ac_head_master) and see
    // every transaction posted against it with a running balance, same
    // idea as IndividualStockReportView but for accounts.db's
    // daybook_summary instead of inventory.db's stock ledger.
    public partial class LedgerReportView : UserControl, IDisposable
    {
        private readonly TaxLedgerRepository _taxLedgerRepo;

        private List<LedgerTransactionReportRow> _allRowsForHead = new(); // full history, unfiltered
        private ObservableCollection<LedgerTransactionReportRow> _displayRows = new();

        private int? _selectedLedgerId;
        private bool _initialized;

        public LedgerReportView()
        {
            InitializeComponent();

            _taxLedgerRepo = new TaxLedgerRepository();

            sfReport.ItemsSource = _displayRows;

            cmbAcHead.ItemsSource = _taxLedgerRepo.GetAcHeadPickerLookups();

            var today = DateTime.Today;
            dpFromDate.SelectedDate = new DateTime(today.Year, today.Month, 1);
            dpToDate.SelectedDate = today;

            // Bridge for Trial Balance / Balance Sheet / Trading P&L's
            // "double-click a row to see it on the Ledger Report" — see
            // EntryEditNavigator's class doc comment for why this is a
            // Subscribe-in-constructor/Unsubscribe-in-Dispose pair rather
            // than Loaded/Unloaded.
            EntryEditNavigator.Subscribe<(string AcHeadCode, string AcHeadName, DateTime FromDate, DateTime ToDate)>(
                EntryChannels.LedgerReport, OnLedgerRequested);

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // No auto-load — nothing to show until an account head is picked.
        }

        /// <summary>Fired via EntryEditNavigator when Trial Balance/Balance
        /// Sheet/Trading P&L asks to jump here for a specific a/c head and
        /// date range.</summary>
        private void OnLedgerRequested((string AcHeadCode, string AcHeadName, DateTime FromDate, DateTime ToDate) request)
        {
            // Head list may have changed since this view was constructed.
            cmbAcHead.ItemsSource = _taxLedgerRepo.GetAcHeadPickerLookups();

            var currentList = cmbAcHead.ItemsSource as List<AcHeadPickerLookup>;
            if (currentList == null) return;

            AcHeadPickerLookup match = null;

            if (!string.IsNullOrWhiteSpace(request.AcHeadCode))
                match = currentList.FirstOrDefault(h => h.AcHeadCode == request.AcHeadCode);

            // Fallback: Balance Sheet / Trading P&L rows only carry a
            // display Particulars string, not an ac_head_code — match by
            // name instead. This also silently no-ops synthetic lines like
            // "Net Profit"/"Gross Profit c/d" that were never real
            // ac_head_master rows to begin with, since nothing in the
            // picker list will match their name.
            if (match == null && !string.IsNullOrWhiteSpace(request.AcHeadName))
                match = currentList.FirstOrDefault(h =>
                    string.Equals(h.AcHeadName, request.AcHeadName, StringComparison.OrdinalIgnoreCase));

            if (match == null) return;

            // Set the date range BEFORE selecting the head — Filter_Changed
            // no-ops while _selectedLedgerId is still null/stale (see its
            // guard below), so this can't trigger a redundant load against
            // the previous head's data with the new dates, or vice versa.
            dpFromDate.SelectedDate = request.FromDate;
            dpToDate.SelectedDate = request.ToDate;

            cmbAcHead.SelectedItem = match; // fires CmbAcHead_SelectionChanged -> LoadReport()
        }

        public void Dispose() =>
            EntryEditNavigator.Unsubscribe<(string AcHeadCode, string AcHeadName, DateTime FromDate, DateTime ToDate)>(
                EntryChannels.LedgerReport, OnLedgerRequested);

        // ════════════════════════════════════════════════════════════════════
        // ACCOUNT HEAD PICKER
        // ════════════════════════════════════════════════════════════════════
        private void CmbAcHead_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            if (cmbAcHead.SelectedItem is not AcHeadPickerLookup selected) return;

            _selectedLedgerId = selected.LedgerId;
            LoadReport();
        }

        // ════════════════════════════════════════════════════════════════════
        // FILTER PANEL
        // ════════════════════════════════════════════════════════════════════
        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized || _selectedLedgerId == null) return;
            ApplyFilterToDisplay();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLedgerId == null)
            {
                MessageBox.Show("Please select an account head first.",
                    "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            LoadReport();
        }

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS — jump to the entry that posted a line. The synthetic
        // Opening Balance row isn't a real posting, so it's a no-op.
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is not LedgerTransactionReportRow row) return;
            if (row.IsOpeningBalance) return;

            EntryDrillDownHelper.HandleRowActivated(
                owner: this,
                transactionType: row.TransactionType,
                entryDate: row.EntryDateValue,
                voucherRef: row.VoucherRef,
                groupSeries: row.Series,
                groupBillNoFrom: row.BillNoFrom,
                groupBillNoTo: row.BillNoTo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "LEDGER REPORT";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Date",      nameof(LedgerTransactionReportRow.EntryDateLabel),   width: 100),
                new ReportColumn("Type",      nameof(LedgerTransactionReportRow.TransactionType),  width: 110),
                new ReportColumn("Narration", nameof(LedgerTransactionReportRow.Narration),         width: 150),
                new ReportColumn("Debit",     nameof(LedgerTransactionReportRow.Debit),             width: 100, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Credit",    nameof(LedgerTransactionReportRow.Credit),            width: 100, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Balance",   nameof(LedgerTransactionReportRow.RunningBalanceDisplay), width: 110, alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>();

            if (cmbAcHead.SelectedItem is AcHeadPickerLookup selected)
                parts.Add($"{selected.AcHeadName} ({selected.AcHeadCode})");

            parts.Add($"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}");

            return string.Join("  |  ", parts);
        }

        private string[] BuildFooterLines() => new[]
        {
            $"Total Debit: {_displayRows.Where(r => !r.IsOpeningBalance).Sum(r => r.DebitAmount):N2}",
            $"Total Credit: {_displayRows.Where(r => !r.IsOpeningBalance).Sum(r => r.CreditAmount):N2}",
            $"Closing Balance: {LedgerTransactionReportRow.FormatCrDr(_displayRows.Count > 0 ? _displayRows.Last().RunningBalance : 0)}"
        };

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_displayRows.Count == 0)
            {
                MessageBox.Show("There is nothing to print. Load the report first.",
                    "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportPrintHelper.Print(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _displayRows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                pageMargin: new Thickness(40, 60, 28, 28),
                baseFontSize: 11.0);
        }

        private void BtnExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_displayRows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Load the report first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportExcelHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _displayRows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle());
        }

        private void BtnPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_displayRows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Load the report first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportPdfHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _displayRows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle());
        }

        // ════════════════════════════════════════════════════════════════════
        // LOAD REPORT
        // ════════════════════════════════════════════════════════════════════
        private void LoadReport()
        {
            if (_selectedLedgerId == null) return;

            try
            {
                // Full unfiltered history — running balance is computed over
                // this, then the date filter is applied on top for display
                // only (see ApplyFilterToDisplay), same pattern as
                // IndividualStockReportView.
                _allRowsForHead = _taxLedgerRepo.GetLedgerHistory(_selectedLedgerId.Value);

                ApplyFilterToDisplay();
                UpdateCurrentSnapshot();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyFilterToDisplay()
        {
            DateTime fromDate = dpFromDate.SelectedDate ?? DateTime.Today;
            DateTime toDate = dpToDate.SelectedDate ?? DateTime.Today;

            var filtered = _allRowsForHead.Where(r =>
                r.EntryDateValue.Date >= fromDate.Date && r.EntryDateValue.Date <= toDate.Date);

            _displayRows.Clear();
            _displayRows.Add(BuildOpeningBalanceRow(fromDate));

            foreach (var r in filtered)
                _displayRows.Add(r);

            UpdateTotalsBar();
        }

        /// <summary>
        /// Synthetic, non-persisted row representing the running balance as
        /// of the instant before <paramref name="fromDate"/> — the balance
        /// already reflects the opening cash baseline transitively wherever
        /// the selected head's own postings tie back to it (e.g. picking
        /// "Cash"), since GetLedgerHistory sums straight off daybook_summary.
        /// </summary>
        private LedgerTransactionReportRow BuildOpeningBalanceRow(DateTime fromDate)
        {
            double openingBalance = _allRowsForHead
                .Where(r => r.EntryDateValue.Date < fromDate.Date)
                .Select(r => (double?)r.RunningBalance)
                .LastOrDefault() ?? 0;

            return new LedgerTransactionReportRow
            {
                SortKey = -1,
                EntryDate = fromDate.ToString("yyyy-MM-dd"),
                TransactionType = "Opening Balance",
                Narration = "Balance b/d",
                DebitAmount = 0,
                CreditAmount = 0,
                RunningBalance = openingBalance,
                IsOpeningBalance = true
            };
        }

        private void UpdateTotalsBar()
        {
            lblTotalDebit.Text = _displayRows.Where(r => !r.IsOpeningBalance).Sum(r => r.DebitAmount).ToString("N2");
            lblTotalCredit.Text = _displayRows.Where(r => !r.IsOpeningBalance).Sum(r => r.CreditAmount).ToString("N2");
            lblClosingBalance.Text = LedgerTransactionReportRow.FormatCrDr(_displayRows.Count > 0 ? _displayRows.Last().RunningBalance : 0);
        }

        private void UpdateCurrentSnapshot()
        {
            lblCurrentBalance.Text = LedgerTransactionReportRow.FormatCrDr(_allRowsForHead.Count > 0 ? _allRowsForHead.Last().RunningBalance : 0);
        }
    }
}