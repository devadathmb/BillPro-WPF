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
    // Sales Report — same reusable report pattern as PurchaseReportView.
    // Filters: From/To bill date (defaults to the current month), a
    // multi-select Series filter (empty selection = all series), and a
    // free-text search across Customer Name / GST / Bill No.
    public partial class SalesReportView : UserControl
    {
        private readonly SalesRepository _salesRepo;
        private readonly SalesSeriesRepository _seriesRepo;
        private ObservableCollection<SalesReportRow> _rows = new();

        private bool _initialized;

        // GST rate keys ("5", "12", "18", ...) actually present (non-zero)
        // across the currently loaded rows, numeric-ascending. Drives both
        // the on-screen grid's dynamic "Sales@X%" columns and the matching
        // Print/PDF/Excel columns, so the two always agree. Recomputed on
        // every LoadReport().
        private List<string> _rateKeys = new();

        // Names of the grid columns LoadReport added dynamically last time
        // (the rate columns + Tax + Others + Total), so the next LoadReport
        // can remove exactly those before adding the fresh set — the 5
        // customer-detail columns declared in XAML are left alone.
        private readonly List<string> _dynamicGridColumnNames = new();

        public SalesReportView()
        {
            InitializeComponent();

            _salesRepo = new SalesRepository();
            _salesRepo.InitializeDatabase();
            _seriesRepo = new SalesSeriesRepository();
            _seriesRepo.InitializeDatabase();

            sfReport.ItemsSource = _rows;
            seriesFilter.LoadSeries(_seriesRepo.GetActive());

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

        private void TxtCustomerSearch_KeyDown(object sender, KeyEventArgs e)
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
            if (sfReport.SelectedItem is SalesReportRow row)
                OpenEditForBill(row.Prefix, row.BillNo);
        }

        private void MenuEditBill_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is SalesReportRow row)
                OpenEditForBill(row.Prefix, row.BillNo);
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is not SalesReportRow row) return;

            try
            {
                var summary = _salesRepo.GetSummary(row.Prefix, row.BillNo);
                if (summary == null)
                {
                    MessageBox.Show("This bill could not be found (it may have been deleted).",
                        "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var items = _salesRepo.GetItems(row.Prefix, row.BillNo);

                var detailWindow = new SalesDetailWindow(summary, items)
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
            if (sfReport.SelectedItem is SalesReportRow row && !string.IsNullOrWhiteSpace(row.FormattedBillNo))
                Clipboard.SetText(row.FormattedBillNo);
        }

        private void OpenEditForBill(string prefix, int billNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Sales Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.SalesEntry, (prefix, billNo));
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "SALES REPORT";

        // Rate key ("5", "12", "2.5"...) -> display header ("Sales@5%").
        // Pulled out so the grid columns and the export columns use exactly
        // the same wording.
        private static string RateColumnHeader(string rateKey) => $"Sales@{rateKey}%";

        private List<ReportColumn> BuildReportColumns()
        {
            var columns = new List<ReportColumn>
            {
                new ReportColumn("Bill Date",  nameof(SalesReportRow.BillDate),        width: 85),
                new ReportColumn("Bill No",    nameof(SalesReportRow.FormattedBillNo), width: 100),
                new ReportColumn("Type",       nameof(SalesReportRow.BillType),        width: 65),
                new ReportColumn("Customer",   nameof(SalesReportRow.CustomerName),    width: 180),
                new ReportColumn("GSTIN/RegNo",nameof(SalesReportRow.CustomerGstNo),   width: 120),
            };

            // One taxable-amount column per GST rate actually present in
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
                    valueSelector: row => ((SalesReportRow)row).TaxByRate.TryGetValue(key, out var v) ? v : 0.0));
            }

            columns.Add(new ReportColumn("Tax", nameof(SalesReportRow.Tax), width: 85, format: "N2", alignment: TextAlignment.Right));
            columns.Add(new ReportColumn("Others", nameof(SalesReportRow.Others), width: 85, format: "N2", alignment: TextAlignment.Right));
            columns.Add(new ReportColumn("Total", nameof(SalesReportRow.Total), width: 100, format: "N2", alignment: TextAlignment.Right));

            return columns;
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>
            {
                $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}"
            };

            var series = seriesFilter.SelectedPrefixes;
            if (series.Count > 0)
                parts.Add($"Series: {string.Join(", ", series)}");

            if (!string.IsNullOrWhiteSpace(txtCustomerSearch.Text))
                parts.Add($"Search: \"{txtCustomerSearch.Text}\"");

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
            lines.Add($"Tax: {_rows.Sum(r => r.Tax):N2}");
            lines.Add($"Others: {_rows.Sum(r => r.Others):N2}");
            lines.Add($"Grand Total: {_rows.Sum(r => r.Total):N2}");
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

        // Rebuilds sfReport's dynamic columns (the per-rate Sales@X%
        // columns, plus Tax/Others/Total) to match the current _rateKeys.
        // The 5 customer-detail columns declared in XAML are never touched.
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

            AddSimple(nameof(SalesReportRow.Tax), "Tax", 90);
            AddSimple(nameof(SalesReportRow.Others), "Others", 90);
            AddSimple(nameof(SalesReportRow.Total), "Total", 120);
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
                var seriesPrefixes = seriesFilter.SelectedPrefixes;

                var summaries = _salesRepo.GetReport(fromDate, toDate, txtCustomerSearch.Text, seriesPrefixes);
                var breakup = _salesRepo.GetTaxRateBreakup(fromDate, toDate, txtCustomerSearch.Text, seriesPrefixes);

                // Pivot the flat (Prefix, BillNo, TaxPercent, TaxableAmount)
                // rows into a per-bill lookup, and collect the full set of
                // rate keys seen anywhere in this report (already excludes
                // rates that net to 0 — GetTaxRateBreakup filters those at
                // the SQL level).
                var byBill = new Dictionary<(string Prefix, int BillNo), Dictionary<string, double>>();
                var rateKeySet = new SortedSet<double>();
                foreach (var b in breakup)
                {
                    string rateKey = b.TaxPercent.ToString("0.##", CultureInfo.InvariantCulture);
                    rateKeySet.Add(b.TaxPercent);

                    var billKey = (b.Prefix, b.BillNo);
                    if (!byBill.TryGetValue(billKey, out var rateDict))
                        byBill[billKey] = rateDict = new Dictionary<string, double>();
                    rateDict[rateKey] = b.TaxableAmount;
                }
                _rateKeys = rateKeySet.Select(r => r.ToString("0.##", CultureInfo.InvariantCulture)).ToList();

                _rows.Clear();
                foreach (var s in summaries)
                {
                    var row = SalesReportRow.FromSummary(s);
                    if (byBill.TryGetValue((s.Prefix, s.BillNo), out var rateDict))
                        row.TaxByRate = rateDict;
                    // Every row carries every active rate key (0 for rates
                    // this particular bill didn't use) so the grid's fixed
                    // "TaxByRate[5]" bindings never hit a missing key.
                    foreach (var key in _rateKeys)
                        if (!row.TaxByRate.ContainsKey(key))
                            row.TaxByRate[key] = 0.0;
                    _rows.Add(row);
                }

                RebuildDynamicGridColumns();

                lblTotalAmount.Text = _rows.Sum(r => r.GrossAmount).ToString("N2");
                lblTotalDiscount.Text = _rows.Sum(r => r.Discount).ToString("N2");
                lblTotalTax.Text = _rows.Sum(r => r.Tax).ToString("N2");
                lblGrandTotal.Text = _rows.Sum(r => r.Total).ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}