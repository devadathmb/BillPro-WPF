using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
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
    // Daybook Report — date-wise ledger view for the accounts sync
    // pipeline. One row per posting leg (Taxable / Tax / Party) from
    // voucher_tax_postings, grouped by EntryDate so each day prints as its
    // own "By .../To ..." block — same shape as the classic manual Day
    // Book printout, sourced via TaxLedgerRepository.GetDaybookEntries.
    public partial class DaybookReportView : UserControl
    {
        private readonly TaxLedgerRepository _taxLedgerRepo;
        private ObservableCollection<DaybookLedgerRow> _rows = new();

        // Full export list, including the synthetic date-header rows
        // GetDaybookEntries inserts at the top of each day. These are NOT
        // filtered out of _rows — the on-screen grid has no live Syncfusion
        // grouping (AllowGrouping="False" in XAML), so the IsDateHeader row
        // IS the date band the user sees, styled via RowStyle/
        // AlternatingRowStyle triggers. _exportRows and _rows end up as the
        // same row set; kept as two fields only because Print/PDF/Excel
        // take the row list independently of the grid's ItemsSource.
        private List<DaybookLedgerRow> _exportRows = new();

        private bool _initialized;

        public DaybookReportView()
        {
            InitializeComponent();

            _taxLedgerRepo = new TaxLedgerRepository();

            sfReport.ItemsSource = _rows;
            // No GroupColumnDescriptions here — the grid uses no live
            // grouping (AllowGrouping="False" in XAML). Each day's date
            // band is the synthetic IsDateHeader row baked in by
            // TaxLedgerRepository.GetDaybookEntries, styled via
            // RowStyle/AlternatingRowStyle triggers + the Account Head
            // column template in XAML. See LoadReport() below.

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

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS — jump to the entry that posted a line. Balance b/f,
        // Day Total, and the date-header row aren't real postings, so they
        // don't do anything on double-click.
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is not DaybookLedgerRow row) return;
            if (row.IsOpeningBalance || row.IsDayTotal || row.IsDateHeader) return;

            EntryDrillDownHelper.HandleRowActivated(
                owner: this,
                transactionType: row.TransactionType,
                entryDate: row.EntryDateValue,
                voucherRef: row.VoucherRef,
                groupSeries: row.GroupSeries,
                groupBillNoFrom: row.GroupBillNoFrom,
                groupBillNoTo: row.GroupBillNoTo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "DAY BOOK";

        // The print/PDF/Excel helpers render a flat table (no live grouping
        // like the on-screen grid), so EntryDate is kept as its own leading
        // column here — it still reads as one block per day because rows
        // are already ordered by EntryDate, then insertion order, from
        // GetDaybookEntries.
        // Tune this if the Date column ever needs more/less room — e.g. the
        // Day Total row's date renders bold/larger than a normal cell, so
        // it needs a bit more width than a plain date value would to avoid
        // wrapping onto two lines ("01 Jul, 2026" / "Wednesday").
        private const double DateColumnWidth = 170;

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Date",         nameof(DaybookLedgerRow.EntryDateLabel),     width: DateColumnWidth),
                new ReportColumn("Account Head",  nameof(DaybookLedgerRow.AccountHeadDisplay), width: 220),
                new ReportColumn("Narration",     nameof(DaybookLedgerRow.Narration),          width: 150),
                new ReportColumn("Receipt",       nameof(DaybookLedgerRow.Receipt),            width: 110, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Payment",       nameof(DaybookLedgerRow.Payment),             width: 110, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle() =>
            $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}";

        // Only real posting rows count toward the grand total — Balance b/f
        // carries a running balance (not a receipt/payment of its own) and
        // Day Total is itself a sum of the rows around it, so both would
        // double- or over-count if included here.
        private IEnumerable<DaybookLedgerRow> RealRows() =>
            _rows.Where(r => !r.IsOpeningBalance && !r.IsDayTotal && !r.IsDateHeader);

        private string[] BuildFooterLines() => new[]
        {
            $"Total Receipt: {RealRows().Sum(r => r.CreditAmount):N2}",
            $"Total Payment: {RealRows().Sum(r => r.DebitAmount):N2}",
            $"Difference: {(RealRows().Sum(r => r.CreditAmount) - RealRows().Sum(r => r.DebitAmount)):N2}",
            $"Closing Balance (Cash in Hand): {_taxLedgerRepo.GetCashInHandBalance(dpToDate.SelectedDate ?? DateTime.Today):N2}"
        };

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_exportRows.Count == 0)
            {
                MessageBox.Show("There is nothing to print. Load the report first.",
                    "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportPrintHelper.Print(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _exportRows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                pageMargin: new Thickness(40, 60, 28, 28),
                baseFontSize: 11.0);
        }

        private void BtnExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_exportRows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Load the report first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportExcelHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _exportRows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle());
        }

        private void BtnPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_exportRows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Load the report first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReportPdfHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: _exportRows,
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

                var entries = _taxLedgerRepo.GetDaybookEntries(fromDate, toDate);

                entries = ReorderByBillNumber(entries);

                // Report-level Opening Balance — one row, always first,
                // showing the balance as of the instant before fromDate.
                // Distinct from the per-day "Balance b/f" rows
                // GetDaybookEntries already inserts at the top of each day:
                // this one sits above the whole day-by-day section (even
                // ahead of the first date-header), so the opening position
                // is visible regardless of whether fromDate itself has any
                // postings. Same idea as LedgerReportView.BuildOpeningBalanceRow.
                // Inserted here (after ReorderByBillNumber, not before) so it
                // never gets swept into a day's GroupBy bucket even if its
                // EntryDate happens to match the first transaction day.
                entries.Insert(0, BuildReportOpeningBalanceRow(fromDate));

                _exportRows = entries;

                _rows.Clear();
                foreach (var e in entries)
                    _rows.Add(e);

                lblTotalReceipt.Text = RealRows().Sum(r => r.CreditAmount).ToString("N2");
                lblTotalPayment.Text = RealRows().Sum(r => r.DebitAmount).ToString("N2");
                lblGrandTotal.Text = (RealRows().Sum(r => r.CreditAmount) - RealRows().Sum(r => r.DebitAmount)).ToString("N2");

                // Cash in hand as at the end of the selected range — computed
                // against the Cash ledger specifically (see
                // TaxLedgerRepository.GetCashInHandBalance), not by summing
                // every ledger's Credit-Debit the way the on-screen Balance
                // b/f rows do; that sum always cancels to a constant for a
                // fully double-entry-balanced voucher regardless of
                // settlement type, so it can't track real cash movement.
                lblClosingBalance.Text = _taxLedgerRepo.GetCashInHandBalance(toDate).ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT-LEVEL OPENING BALANCE — one synthetic row shown before the
        // day-by-day section, holding the balance as of the instant before
        // the selected From date. Uses GetCashInHandBalance(fromDate - 1),
        // the same figure BtnDisplay/LoadReport already use for the
        // "Closing Balance" footer at toDate, and the same one
        // GetDaybookEntries seeds its per-day running balance from
        // internally (GetBalanceBefore) — so this row's amount always ties
        // out with whatever the first day's own Balance b/f shows.
        // ════════════════════════════════════════════════════════════════════
        private DaybookLedgerRow BuildReportOpeningBalanceRow(DateTime fromDate)
        {
            double openingBalance = _taxLedgerRepo.GetCashInHandBalance(fromDate.AddDays(-1));

            return new DaybookLedgerRow
            {
                EntryDate = fromDate.ToString("yyyy-MM-dd"),
                AcHeadName = "Balance b/f",
                TransactionType = "OpeningBalance",
                VoucherRef = "",
                DebitAmount = 0,
                CreditAmount = openingBalance,
                SortKey = -1,
                IsOpeningBalance = true,
                IsReportOpeningBalance = true
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // BILL-NUMBER ORDERING — within each day, ordinary posting rows are
        // reordered by the numeric bill/voucher number embedded in Narration
        // (e.g. "PUR-10" → 10, "C-1001-26-27" → 1001, "SR-1 - SR-3" → 1)
        // instead of insertion order. The date-header row, Balance b/f, and
        // Day Total all keep their fixed position (start/end of the day) —
        // only the real transaction rows in between get reordered.
        // ════════════════════════════════════════════════════════════════════
        private static readonly Regex BillNumberPattern = new(@"\d+", RegexOptions.Compiled);

        private static int ExtractBillNumber(string narration)
        {
            if (string.IsNullOrEmpty(narration)) return int.MaxValue;

            var match = BillNumberPattern.Match(narration);
            return match.Success && int.TryParse(match.Value, out int n) ? n : int.MaxValue;
        }

        private static List<DaybookLedgerRow> ReorderByBillNumber(List<DaybookLedgerRow> entries)
        {
            var result = new List<DaybookLedgerRow>();

            foreach (var day in entries.GroupBy(r => r.EntryDate))
            {
                var dateHeader = day.Where(r => r.IsDateHeader);
                var openingBalance = day.Where(r => r.IsOpeningBalance);
                var dayTotal = day.Where(r => r.IsDayTotal);
                var realRows = day
                    .Where(r => !r.IsDateHeader && !r.IsOpeningBalance && !r.IsDayTotal)
                    .OrderBy(r => ExtractBillNumber(r.Narration))
                    .ThenBy(r => r.SortKey);

                result.AddRange(dateHeader);
                result.AddRange(openingBalance);
                result.AddRange(realRows);
                result.AddRange(dayTotal);
            }

            return result;
        }
    }
}