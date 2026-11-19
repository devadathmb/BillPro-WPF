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
    // Sundry Creditors report — every vendor/customer a/c head with a debit
    // balance as on a chosen date. See SundryPartyRepository for why this
    // is a balance-sign split, not a Customer/Vendor tag split.
    public partial class SundryCreditorsReportView : UserControl
    {
        private readonly SundryPartyRepository _repo = new();

        private ObservableCollection<SundryPartyReportRow> _displayRows = new();
        private bool _initialized;

        public SundryCreditorsReportView()
        {
            InitializeComponent();

            sfReport.ItemsSource = _displayRows;
            dpAsOn.SelectedDate = DateTime.Today;

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e) => LoadReport();

        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized) return;
            LoadReport();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS — jump to the Ledger Report for this a/c head. From
        // date is pinned to the financial-year start; FY handling isn't
        // implemented yet, so this assumes an April-start year as a
        // placeholder (see GetFinancialYearStart) until that lands.
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is not SundryPartyReportRow row) return;

            DateTime asOnDate = dpAsOn.SelectedDate ?? DateTime.Today;
            DateTime fyStart = GetFinancialYearStart(asOnDate);

            MainWindow.Instance?.OpenOrActivateTitle("Ledger (Individual Heads)");
            EntryEditNavigator.RequestEdit(
                EntryChannels.LedgerReport,
                (AcHeadCode: row.AcHeadCode, AcHeadName: row.AcHeadName, FromDate: fyStart, ToDate: asOnDate));
        }

        /// <summary>
        /// Placeholder until real financial-year configuration exists —
        /// assumes an April-to-March year (1-Apr on/before <paramref name="asOn"/>).
        /// Replace with the real FY lookup once that's implemented.
        /// </summary>
        private static DateTime GetFinancialYearStart(DateTime asOn) =>
            new DateTime(asOn.Month >= 4 ? asOn.Year : asOn.Year - 1, 4, 1);

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "SUNDRY CREDITORS";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Code",    nameof(SundryPartyReportRow.AcHeadCode), width: 90),
                new ReportColumn("Name",    nameof(SundryPartyReportRow.AcHeadName), width: 220),
                new ReportColumn("Credit",  nameof(SundryPartyReportRow.Balance),    width: 140, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle() =>
            $"As on {(dpAsOn.SelectedDate ?? DateTime.Today):dd-MMM-yyyy}";

        private string[] BuildFooterLines() => new[]
        {
            $"Total Credit: {_displayRows.Sum(r => r.Balance):N2}"
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
            try
            {
                DateTime asOnDate = dpAsOn.SelectedDate ?? DateTime.Today;
                var rows = _repo.GetCreditors(asOnDate);

                _displayRows.Clear();
                foreach (var r in rows)
                    _displayRows.Add(r);

                lblGrandTotal.Text = _displayRows.Sum(r => r.Balance).ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}