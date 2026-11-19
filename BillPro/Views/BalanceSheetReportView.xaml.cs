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
    // Balance Sheet — closing balance, as at a chosen date, of every
    // ac_head in the Balance Sheet group (ac_group_id = 0), split
    // Liability / Asset side by side, with the financial year's Net
    // Profit/Loss folded in so the two totals balance. One row per pair
    // of (Liability item, Asset item) — same "two lists zipped together"
    // shape as the legacy printed sheet — sourced via
    // TaxLedgerRepository.GetBalanceSheet.
    public partial class BalanceSheetReportView : UserControl
    {
        private readonly TaxLedgerRepository _taxLedgerRepo;
        private ObservableCollection<BalanceSheetRow> _rows = new();

        // sfLiability and sfAsset both bind to the same _rows collection —
        // each row already carries both sides' data, so the two grids are
        // really just two different column projections of one row set,
        // not two independent lists.

        private bool _initialized;

        public BalanceSheetReportView()
        {
            InitializeComponent();

            _taxLedgerRepo = new TaxLedgerRepository();

            sfLiability.ItemsSource = _rows;
            sfAsset.ItemsSource = _rows;

            dpAsAt.SelectedDate = DateTime.Today;

            // Double-click a row on either side to see that a/c head's
            // full history on the Ledger Report. Separate handlers per
            // grid because sfLiability/sfAsset share one BalanceSheetRow
            // per row but each side names a completely different a/c
            // head — see EntryDrillDownHelper's class doc comment for the
            // broader convention this follows.
            sfLiability.MouseDoubleClick += SfLiability_MouseDoubleClick;
            sfAsset.MouseDoubleClick += SfAsset_MouseDoubleClick;

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(LoadReport));
        }

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS — jump to the Ledger Report. BalanceSheetRow only
        // carries a display Particulars string per side (not an
        // ac_head_code — see TaxLedgerModels.cs), so the name is passed
        // through and matched on the Ledger Report side instead; this also
        // means synthetic lines like "Net Profit"/"Net Loss" harmlessly
        // no-op there since they don't match any real a/c head. The
        // Total row and a side with no item for this row (null
        // Particulars) are both no-ops.
        //
        // "As at" only gives one date, not a range — the Ledger Report
        // needs a From/To — so this uses the same financial-year-start
        // convention as Trial Balance's SetFromDateForFinancialYear,
        // through to the As At date itself.
        // ════════════════════════════════════════════════════════════════════
        private void SfLiability_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfLiability.SelectedItem is not BalanceSheetRow row) return;
            if (row.IsTotalRow || string.IsNullOrWhiteSpace(row.LiabilityParticulars)) return;

            OpenLedgerReport(row.LiabilityParticulars);
        }

        private void SfAsset_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfAsset.SelectedItem is not BalanceSheetRow row) return;
            if (row.IsTotalRow || string.IsNullOrWhiteSpace(row.AssetParticulars)) return;

            OpenLedgerReport(row.AssetParticulars);
        }

        private void OpenLedgerReport(string acHeadName)
        {
            DateTime asAt = dpAsAt.SelectedDate ?? DateTime.Today;
            int fyStartYear = asAt.Month >= 4 ? asAt.Year : asAt.Year - 1;
            DateTime fyStart = new DateTime(fyStartYear, 4, 1);

            MainWindow.Instance?.OpenOrActivateTitle("Ledger (Individual Heads)");
            EntryEditNavigator.RequestEdit(EntryChannels.LedgerReport,
                (AcHeadCode: (string)null, AcHeadName: acHeadName,
                 FromDate: fyStart, ToDate: asAt));
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
        private const string ReportTitle = "BALANCE SHEET";

        private List<ReportColumn> BuildReportColumns()
        {
            // Headers are "Particulars"/"Amount" on both sides now — the
            // "Liability"/"Asset" labels moved up a tier into the group
            // header (see BuildColumnGroups), matching the big LIABILITY/
            // ASSET headings already shown above each SfDataGrid in the
            // form, with Particulars/Amount as the sub-header underneath.
            return new List<ReportColumn>
            {
                new ReportColumn("Particulars",  nameof(BalanceSheetRow.LiabilityParticulars), width: 220),
                new ReportColumn("Amount",       nameof(BalanceSheetRow.LiabilityAmount),       width: 120, format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Particulars",  nameof(BalanceSheetRow.AssetParticulars),      width: 220),
                new ReportColumn("Amount",       nameof(BalanceSheetRow.AssetAmount),            width: 120, format: "N2", alignment: TextAlignment.Right),
            };
        }

        // Two groups of two columns each — LIABILITY covers the Liability
        // Particulars/Amount pair, ASSET covers the Asset pair. Also gets
        // a vertical divider drawn between them by Print/PDF/Excel,
        // matching the black Border already separating sfLiability/sfAsset
        // on screen.
        private List<ReportColumnGroup> BuildColumnGroups() => new List<ReportColumnGroup>
        {
            new ReportColumnGroup("LIABILITY", 2),
            new ReportColumnGroup("ASSET", 2),
        };

        private string BuildSubTitle() =>
            $"As at {dpAsAt.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}";

        // Parses the "N2"-formatted totals labels back to decimal for the
        // difference calculation — same labels already shown in the form,
        // so this can never drift from what's on screen.
        private double GetTotalLiabilityValue() =>
            double.TryParse(lblTotalLiability.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double v) ? v : 0;

        private double GetTotalAssetValue() =>
            double.TryParse(lblTotalAsset.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double v) ? v : 0;

        private string[] BuildFooterLines()
        {
            var lines = new List<string>
            {
                $"Total Liability: {lblTotalLiability.Text}",
                $"Total Asset: {lblTotalAsset.Text}"
            };

            // Only included when there IS a difference — an exactly
            // balanced Balance Sheet (the normal case, since Net
            // Profit/Loss is folded in specifically to make both sides
            // tie out) shows no difference line at all, same as the form.
            double difference = GetTotalLiabilityValue() - GetTotalAssetValue();
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
                DateTime asAt = dpAsAt.SelectedDate ?? DateTime.Today;

                var entries = _taxLedgerRepo.GetBalanceSheet(asAt);

                _rows.Clear();
                foreach (var r in entries)
                    _rows.Add(r);

                // Both grids share the same _rows collection, but the total
                // figures are read off the dedicated "Total" row rather
                // than re-summed here, so the footer can never drift from
                // what the grid itself displays.
                var totalRow = entries.LastOrDefault(r => r.IsTotalRow);
                double totalLiability = totalRow?.LiabilityAmount ?? 0;
                double totalAsset = totalRow?.AssetAmount ?? 0;
                lblTotalLiability.Text = totalLiability.ToString("N2");
                lblTotalAsset.Text = totalAsset.ToString("N2");

                // Difference textblock — only shown when the two sides
                // genuinely don't tie out (should be the rare case, since
                // Net Profit/Loss is folded in specifically to balance
                // them). Zero difference hides the row entirely, same as
                // BuildFooterLines omits the "Difference" line in that case.
                double difference = totalLiability - totalAsset;
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