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
    // Trial Balance — closing balance of every ac_head "as at" a chosen
    // date, split Debit/Credit, zero-balance heads dropped. One row per
    // account head (no date-wise grouping like Daybook), sourced via
    // TaxLedgerRepository.GetTrialBalance.
    public partial class TrialBalanceReportView : UserControl
    {
        private readonly TaxLedgerRepository _taxLedgerRepo;
        private ObservableCollection<TrialBalanceRow> _rows = new();

        // Same row list feeds the grid and Print/PDF/Excel — no synthetic
        // header rows here (unlike Daybook), so there's no need for a
        // separate _exportRows field.

        private bool _initialized;

        public TrialBalanceReportView()
        {
            InitializeComponent();

            _taxLedgerRepo = new TaxLedgerRepository();

            sfReport.ItemsSource = _rows;

            dpToDate.SelectedDate = DateTime.Today;
            SetFromDateForFinancialYear(DateTime.Today);

            // Double-click a row to see that a/c head's full history on the
            // Ledger Report, over the same From/To range shown here — see
            // EntryDrillDownHelper's class doc comment for the broader
            // convention this follows.
            sfReport.MouseDoubleClick += SfReport_MouseDoubleClick;

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(LoadReport));
        }

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS — jump to the Ledger Report for this a/c head. The
        // synthetic Total row isn't a real head, so it's a no-op.
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is not TrialBalanceRow row) return;
            if (row.IsTotalRow) return;

            MainWindow.Instance?.OpenOrActivateTitle("Ledger (Individual Heads)");
            EntryEditNavigator.RequestEdit(EntryChannels.LedgerReport,
                (AcHeadCode: row.AcHeadCode, AcHeadName: row.AcHeadName,
                 FromDate: dpFromDate.SelectedDate ?? DateTime.Today,
                 ToDate: dpToDate.SelectedDate ?? DateTime.Today));
        }

        // ════════════════════════════════════════════════════════════════════
        // FILTER PANEL
        // ════════════════════════════════════════════════════════════════════

        // dpFromDate is disabled — display only, for parity with the legacy
        // screen. Re-derived from "As At" every time it changes; never read
        // by LoadReport/GetTrialBalance.
        private void SetFromDateForFinancialYear(DateTime asAt)
        {
            int fyStartYear = asAt.Month >= 4 ? asAt.Year : asAt.Year - 1;
            dpFromDate.SelectedDate = new DateTime(fyStartYear, 4, 1);
        }

        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized) return;
            SetFromDateForFinancialYear(dpToDate.SelectedDate ?? DateTime.Today);
            LoadReport();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "TRIAL BALANCE";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Code",            nameof(TrialBalanceRow.AcHeadCode), width: 90),
                new ReportColumn("Head of Account",  nameof(TrialBalanceRow.AcHeadName), width: 280),
                new ReportColumn("Debit",            nameof(TrialBalanceRow.Debit),  width: 130, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Credit",           nameof(TrialBalanceRow.Credit), width: 130, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle() =>
            $"As at {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}";

        private IEnumerable<TrialBalanceRow> RealRows() => _rows.Where(r => !r.IsTotalRow);

        private string[] BuildFooterLines() => new[]
        {
            $"Total Debit: {RealRows().Sum(r => r.Debit):N2}",
            $"Total Credit: {RealRows().Sum(r => r.Credit):N2}"
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
                rows: _rows.ToList(),
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
                rows: _rows.ToList(),
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
                rows: _rows.ToList(),
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
                DateTime asAt = dpToDate.SelectedDate ?? DateTime.Today;

                var entries = _taxLedgerRepo.GetTrialBalance(asAt);

                _rows.Clear();
                foreach (var r in entries)
                    _rows.Add(r);

                double totalDebit = entries.Sum(r => r.Debit);
                double totalCredit = entries.Sum(r => r.Credit);

                // Appended row, not persisted — same idea as Daybook's
                // BuildDayTotalRow, just built here since there's only one
                // total for the whole report rather than one per day.
                if (entries.Count > 0)
                {
                    _rows.Add(new TrialBalanceRow
                    {
                        IsTotalRow = true,
                        Debit = totalDebit,
                        Credit = totalCredit
                    });
                }

                lblTotalDebit.Text = totalDebit.ToString("N2");
                lblTotalCredit.Text = totalCredit.ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}