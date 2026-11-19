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
using Syncfusion.UI.Xaml.Grid;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Sales Return Report — one row per return entry (no series filter,
    // since sales returns don't use the prefix/series system at all). Same
    // taxable-by-rate + Tax + Others + Total layout as PurchaseReportView/
    // SalesReportView/PurchaseReturnReportView.
    public partial class SalesReturnReportView : UserControl
    {
        private readonly SalesReturnRepository _repo;
        private ObservableCollection<SalesReturnReportRow> _rows = new();

        private bool _initialized;

        // Tax rate keys ("5", "12", "18", ...) actually present (non-zero)
        // across the currently loaded rows, numeric-ascending. Drives both
        // the on-screen grid's dynamic "SalesReturn@X%" columns and the
        // matching Print/PDF/Excel columns. Recomputed on every LoadReport().
        private List<string> _rateKeys = new();

        // Names of the grid columns LoadReport added dynamically last time,
        // so the next LoadReport can remove exactly those before adding the
        // fresh set — the 4 customer-detail columns declared in XAML are
        // left alone.
        private readonly List<string> _dynamicGridColumnNames = new();

        public SalesReturnReportView()
        {
            InitializeComponent();

            _repo = new SalesReturnRepository();
            _repo.InitializeDatabase();

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

        // ════════════════════════════════════════════════════════════════
        // FILTER PANEL
        // ════════════════════════════════════════════════════════════════
        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized) return;
            LoadReport();
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                LoadReport();
                e.Handled = true;
            }
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════
        // ROW ACTIONS
        // ════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is SalesReturnReportRow row)
                OpenEditForEntry(row.EntryNo);
        }

        private void MenuEditEntry_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is SalesReturnReportRow row)
                OpenEditForEntry(row.EntryNo);
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is not SalesReturnReportRow row) return;

            try
            {
                var summary = _repo.GetSummary(row.EntryNo);
                if (summary == null)
                {
                    MessageBox.Show("This return entry could not be found (it may have been deleted).",
                        "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var items = _repo.GetItems(row.EntryNo);

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

        private void MenuCopyEntryNo_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is SalesReturnReportRow row && !string.IsNullOrWhiteSpace(row.FormattedEntryNo))
                Clipboard.SetText(row.FormattedEntryNo);
        }

        private void OpenEditForEntry(int entryNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Sales Return Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.SalesReturnEntry, entryNo);
        }

        // ════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════
        private const string ReportTitle = "SALES RETURN REPORT";

        // Rate key ("5", "12", "2.5"...) -> display header
        // ("SalesReturn@5%"). Pulled out so the grid columns and the export
        // columns use exactly the same wording.
        private static string RateColumnHeader(string rateKey) => $"Sales Ret@{rateKey}%";

        private List<ReportColumn> BuildReportColumns()
        {
            var columns = new List<ReportColumn>
            {
                new ReportColumn("Date",         nameof(SalesReturnReportRow.EntryDate),        width: 85),
                new ReportColumn("Entry No",     nameof(SalesReturnReportRow.FormattedEntryNo), width: 85),
                new ReportColumn("Against Bill", nameof(SalesReturnReportRow.OriginalBillNo),   width: 100),
                new ReportColumn("Customer",     nameof(SalesReturnReportRow.CustomerName),     width: 170),
            };

            // One taxable-amount column per tax rate actually present in
            // this report (see LoadReport / _rateKeys) — a plain reflection
            // ReportColumn can't back these since the property is a
            // Dictionary<string,double> keyed dynamically, so they go
            // through ValueSelector instead.
            foreach (var rateKey in _rateKeys)
            {
                string key = rateKey; // capture for the closure
                columns.Add(new ReportColumn(
                    RateColumnHeader(key), $"TaxByRate_{key}", width: 90, format: "N2",
                    alignment: TextAlignment.Right,
                    valueSelector: row => ((SalesReturnReportRow)row).TaxByRate.TryGetValue(key, out var v) ? v : 0.0));
            }

            columns.Add(new ReportColumn("Tax", nameof(SalesReturnReportRow.TaxAmount), width: 85, format: "N2", alignment: TextAlignment.Right));
            columns.Add(new ReportColumn("Others", nameof(SalesReturnReportRow.Others), width: 85, format: "N2", alignment: TextAlignment.Right));
            columns.Add(new ReportColumn("Total", nameof(SalesReturnReportRow.NetTotal), width: 100, format: "N2", alignment: TextAlignment.Right));

            return columns;
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>
            {
                $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}"
            };

            if (!string.IsNullOrWhiteSpace(txtSearch.Text))
                parts.Add($"Search: \"{txtSearch.Text}\"");

            return string.Join("  |  ", parts);
        }

        private string[] BuildFooterLines()
        {
            var lines = new List<string>();
            foreach (var rateKey in _rateKeys)
            {
                double total = _rows.Sum(r => r.TaxByRate.TryGetValue(rateKey, out var v) ? v : 0.0);
                lines.Add($"{RateColumnHeader(rateKey)}: {total:N2}");
            }
            lines.Add($"Tax: {_rows.Sum(r => r.TaxAmount):N2}");
            lines.Add($"Others: {_rows.Sum(r => r.Others):N2}");
            lines.Add($"Grand Total: {_rows.Sum(r => r.NetTotal):N2}");
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

        // Rebuilds sfReport's dynamic columns (the per-rate SalesReturn@X%
        // columns, plus Tax/Others/Total) to match the current _rateKeys.
        // The 4 customer-detail columns declared in XAML are never touched.
        private void RebuildDynamicGridColumns()
        {
            foreach (var name in _dynamicGridColumnNames)
            {
                var existing = sfReport.Columns.FirstOrDefault(c => c.MappingName == name);
                if (existing != null) sfReport.Columns.Remove(existing);
            }
            _dynamicGridColumnNames.Clear();

            foreach (var rateKey in _rateKeys)
            {
                string mapping = $"TaxByRate[{rateKey}]";
                sfReport.Columns.Add(new GridNumericColumn
                {
                    MappingName = mapping,
                    HeaderText = RateColumnHeader(rateKey),
                    Width = 100,
                    NumberDecimalDigits = 2
                });
                _dynamicGridColumnNames.Add(mapping);
            }

            void AddSimple(string mapping, string header, double width)
            {
                sfReport.Columns.Add(new GridNumericColumn
                {
                    MappingName = mapping,
                    HeaderText = header,
                    Width = width,
                    NumberDecimalDigits = 2
                });
                _dynamicGridColumnNames.Add(mapping);
            }

            AddSimple(nameof(SalesReturnReportRow.TaxAmount), "Tax", 90);
            AddSimple(nameof(SalesReturnReportRow.Others), "Others", 90);
            AddSimple(nameof(SalesReturnReportRow.NetTotal), "Total", 120);
        }

        // ════════════════════════════════════════════════════════════════
        // LOAD REPORT
        // ════════════════════════════════════════════════════════════════
        private void LoadReport()
        {
            try
            {
                DateTime fromDate = dpFromDate.SelectedDate ?? DateTime.Today;
                DateTime toDate = dpToDate.SelectedDate ?? DateTime.Today;

                var results = _repo.GetReport(fromDate, toDate, txtSearch.Text);
                var breakup = _repo.GetTaxRateBreakup(fromDate, toDate, txtSearch.Text);

                // Pivot the flat (EntryNo, TaxPercent, TaxableAmount) rows
                // into a per-entry lookup, and collect the full set of rate
                // keys seen anywhere in this report.
                var byEntry = new Dictionary<int, Dictionary<string, double>>();
                var rateKeySet = new SortedSet<double>();
                foreach (var b in breakup)
                {
                    string rateKey = b.TaxPercent.ToString("0.##", CultureInfo.InvariantCulture);
                    rateKeySet.Add(b.TaxPercent);

                    if (!byEntry.TryGetValue(b.EntryNo, out var rateDict))
                        byEntry[b.EntryNo] = rateDict = new Dictionary<string, double>();
                    rateDict[rateKey] = b.TaxableAmount;
                }
                _rateKeys = rateKeySet.Select(r => r.ToString("0.##", CultureInfo.InvariantCulture)).ToList();

                _rows.Clear();
                foreach (var r in results)
                {
                    if (byEntry.TryGetValue(r.EntryNo, out var rateDict))
                        r.TaxByRate = rateDict;
                    // Every row carries every active rate key (0 for rates
                    // this particular entry didn't use) so the grid's fixed
                    // "TaxByRate[5]" bindings never hit a missing key.
                    foreach (var key in _rateKeys)
                        if (!r.TaxByRate.ContainsKey(key))
                            r.TaxByRate[key] = 0.0;
                    _rows.Add(r);
                }

                RebuildDynamicGridColumns();

                lblTotalAmount.Text = _rows.Sum(r => r.GrossAmount).ToString("N2");
                lblTotalTax.Text = _rows.Sum(r => r.TaxAmount).ToString("N2");
                lblTotalOthers.Text = _rows.Sum(r => r.Others).ToString("N2");
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