using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Cash Book Report — date-wise view of the Cash ledger's own
    // movements only, one row per leg from cash_transactions, grouped by
    // EntryDate the same way DaybookReportView groups daybook_summary —
    // same "By .../To ..." classic Day Book printout shape, just scoped
    // to Cash instead of every other account head. Sourced via
    // TaxLedgerRepository.GetCashbookEntries.
    public partial class CashbookReportView : UserControl
    {
        private readonly TaxLedgerRepository _taxLedgerRepo;
        private ObservableCollection<CashbookLedgerRow> _rows = new();

        // Same reasoning as DaybookReportView._exportRows: the on-screen
        // grid has no live Syncfusion grouping (AllowGrouping="False" in
        // XAML), so the IsDateHeader row IS the date band the user sees.
        // _exportRows and _rows end up as the same row set; kept as two
        // fields only because Print/PDF/Excel take the row list
        // independently of the grid's ItemsSource.
        private List<CashbookLedgerRow> _exportRows = new();

        private bool _initialized;

        public CashbookReportView()
        {
            InitializeComponent();

            _taxLedgerRepo = new TaxLedgerRepository();

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

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "CASH BOOK";

        // Same reasoning as DaybookReportView.DateColumnWidth — the Day
        // Total row's date renders bold/larger than a normal cell, so it
        // needs a bit more width than a plain date value to avoid
        // wrapping onto two lines ("01 Jul, 2026" / "Wednesday").
        private const double DateColumnWidth = 170;

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Date",         nameof(CashbookLedgerRow.EntryDateLabel),     width: DateColumnWidth),
                new ReportColumn("Particulars",  nameof(CashbookLedgerRow.AccountHeadDisplay), width: 220),
                new ReportColumn("Narration",     nameof(CashbookLedgerRow.Narration),          width: 150),
                new ReportColumn("Receipt",       nameof(CashbookLedgerRow.Receipt),            width: 110, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Payment",       nameof(CashbookLedgerRow.Payment),             width: 110, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle() =>
            $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}";

        // Only real posting rows count toward the grand total — Balance b/f
        // carries a running balance (not a receipt/payment of its own) and
        // Day Total is itself a sum of the rows around it, so both would
        // double- or over-count if included here. Same convention as
        // DaybookReportView.RealRows.
        private IEnumerable<CashbookLedgerRow> RealRows() =>
            _rows.Where(r => !r.IsOpeningBalance && !r.IsDayTotal && !r.IsDateHeader);

        private string[] BuildFooterLines() => new[]
        {
            $"Total Receipt: {RealRows().Sum(r => r.DebitAmount):N2}",
            $"Total Payment: {RealRows().Sum(r => r.CreditAmount):N2}",
            $"Difference: {(RealRows().Sum(r => r.DebitAmount) - RealRows().Sum(r => r.CreditAmount)):N2}",
            $"Closing Balance (Cash in Hand): {_taxLedgerRepo.GetCashBookClosingBalance(dpToDate.SelectedDate ?? DateTime.Today):N2}"
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

                var entries = _taxLedgerRepo.GetCashbookEntries(fromDate, toDate);

                entries = ReorderByBillNumber(entries);

                // Report-level Opening Balance — one row, always first,
                // showing the cash-in-hand balance as of the instant before
                // fromDate. Distinct from the per-day "Balance b/f" rows
                // GetCashbookEntries already inserts at the top of each day:
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

                lblTotalReceipt.Text = RealRows().Sum(r => r.DebitAmount).ToString("N2");
                lblTotalPayment.Text = RealRows().Sum(r => r.CreditAmount).ToString("N2");
                lblGrandTotal.Text = (RealRows().Sum(r => r.DebitAmount) - RealRows().Sum(r => r.CreditAmount)).ToString("N2");

                // Deliberately NOT GetCashInHandBalance (that derives Cash
                // from the Day Book's OTHER ledgers, so it always agrees
                // with the Day Book by construction). This is the Cash
                // Book's own, independent figure sourced only from
                // cash_transactions — if the two ever disagree, that's a
                // real discrepancy worth investigating, not a display bug.
                lblClosingBalance.Text = _taxLedgerRepo.GetCashBookClosingBalance(toDate).ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT-LEVEL OPENING BALANCE — one synthetic row shown before the
        // day-by-day section, holding the cash-in-hand balance as of the
        // instant before the selected From date. Uses
        // GetCashBookClosingBalance(fromDate - 1), the same figure
        // LoadReport's "Closing Balance" footer uses at toDate, and the
        // same one GetCashbookEntries seeds its per-day running balance
        // from internally (GetCashBalanceBefore) — so this row's amount
        // always ties out with whatever the first day's own Balance b/f
        // shows.
        // ════════════════════════════════════════════════════════════════════
        private CashbookLedgerRow BuildReportOpeningBalanceRow(DateTime fromDate)
        {
            double openingBalance = _taxLedgerRepo.GetCashBookClosingBalance(fromDate.AddDays(-1));

            return new CashbookLedgerRow
            {
                EntryDate = fromDate.ToString("yyyy-MM-dd"),
                TransactionType = "Balance b/f",
                VoucherRef = "",
                DebitAmount = openingBalance,
                CreditAmount = 0,
                SortKey = -1,
                IsOpeningBalance = true,
                IsReportOpeningBalance = true
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // BILL-NUMBER ORDERING — same convention as DaybookReportView:
        // within each day, ordinary posting rows are reordered by the
        // numeric bill/voucher number embedded in Narration instead of
        // insertion order. The date-header row, Balance b/f, and Day
        // Total all keep their fixed position (start/end of the day) —
        // only the real transaction rows in between get reordered.
        // ════════════════════════════════════════════════════════════════════
        private static readonly Regex BillNumberPattern = new(@"\d+", RegexOptions.Compiled);

        private static int ExtractBillNumber(string narration)
        {
            if (string.IsNullOrEmpty(narration)) return int.MaxValue;

            var match = BillNumberPattern.Match(narration);
            return match.Success && int.TryParse(match.Value, out int n) ? n : int.MaxValue;
        }

        private static List<CashbookLedgerRow> ReorderByBillNumber(List<CashbookLedgerRow> entries)
        {
            var result = new List<CashbookLedgerRow>();

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