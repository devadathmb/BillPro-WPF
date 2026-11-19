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
    // Same reusable report pattern as CurrentStockReportView — filters are
    // From/To purchase date (defaults to the current month) plus a free-text
    // search across Supplier Name / GSTIN / Bill No. LoadReport() re-runs
    // automatically when either date changes, same as the stock report.
    public partial class PurchaseReportView : UserControl
    {
        private readonly PurchaseRepository _purchaseRepo;
        private ObservableCollection<PurchaseReportRow> _rows = new();

        private bool _initialized;

        // Tax rate keys ("5", "12", "18", ...) actually present (non-zero)
        // across the currently loaded rows, numeric-ascending. Drives both
        // the on-screen grid's dynamic "Purchase@X%" columns and the
        // matching Print/PDF/Excel columns. Recomputed on every LoadReport().
        private List<string> _rateKeys = new();

        // Names of the grid columns LoadReport added dynamically last time
        // (the rate columns + Tax + Others + Total), so the next LoadReport
        // can remove exactly those before adding the fresh set — the 5
        // supplier-detail columns declared in XAML are left alone.
        private readonly List<string> _dynamicGridColumnNames = new();

        public PurchaseReportView()
        {
            InitializeComponent();

            _purchaseRepo = new PurchaseRepository();
            _purchaseRepo.InitializeDatabase();

            sfReport.ItemsSource = _rows;

            // Default to the current month — narrow enough to load fast,
            // wide enough to be useful without touching a filter first.
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
        // ROW ACTIONS — right-click menu + double-click
        // Double-click is the primary discoverability path (most users try it
        // before right-click); the context menu covers the same "Edit"/"View
        // Details" actions plus "Copy Bill No" for quick reference elsewhere.
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is PurchaseReportRow row)
                OpenEditForBill(row.PurchaseNo);
        }

        private void MenuEditBill_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is PurchaseReportRow row)
                OpenEditForBill(row.PurchaseNo);
        }

        private void MenuViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is not PurchaseReportRow row) return;

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
            if (sfReport.SelectedItem is PurchaseReportRow row && !string.IsNullOrWhiteSpace(row.BillNo))
                Clipboard.SetText(row.BillNo);
        }

        // Hands the purchase number off to PurchaseEntryView via the generic
        // EntryEditNavigator bridge, on the PurchaseEntry channel.
        private void OpenEditForBill(int purchaseNo)
        {
            // Make sure the Purchase Entry tab exists and is focused before
            // handing off the bill number — EntryEditNavigator only reaches
            // an already-subscribed PurchaseEntryView instance.
            MainWindow.Instance?.OpenOrActivateTitle("Purchase Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.PurchaseEntry, purchaseNo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "PURCHASE REPORT";

        // Rate key ("5", "12", "2.5"...) -> display header ("Purchase@5%").
        // Pulled out so the grid columns and the export columns use exactly
        // the same wording.
        private static string RateColumnHeader(string rateKey) => $"Pur@{rateKey}%";

        private List<ReportColumn> BuildReportColumns()
        {
            var columns = new List<ReportColumn>
            {
                new ReportColumn("Pur Date",      nameof(PurchaseReportRow.PurchaseDate), width: 95),
                new ReportColumn("Bill No",       nameof(PurchaseReportRow.BillNo),       width: 90),
                new ReportColumn("Bill Date",     nameof(PurchaseReportRow.BillDate),     width: 95),
                new ReportColumn("Supplier Name", nameof(PurchaseReportRow.VendorName),   width: 170),
                new ReportColumn("GSTIN/RegNo",   nameof(PurchaseReportRow.GstNo),        width: 130),
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
                    valueSelector: row => ((PurchaseReportRow)row).TaxByRate.TryGetValue(key, out var v) ? v : 0.0));
            }

            columns.Add(new ReportColumn("Tax", nameof(PurchaseReportRow.Tax), width: 85, format: "N2", alignment: TextAlignment.Right));
            columns.Add(new ReportColumn("Others", nameof(PurchaseReportRow.Others), width: 85, format: "N2", alignment: TextAlignment.Right));
            columns.Add(new ReportColumn("Total", nameof(PurchaseReportRow.Total), width: 100, format: "N2", alignment: TextAlignment.Right));

            return columns;
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

        private string[] BuildFooterLines()
        {
            var lines = new List<string>();
            foreach (var rateKey in _rateKeys)
            {
                decimal total = _rows.Sum(r => r.TaxByRate.TryGetValue(rateKey, out var v) ? (decimal)v : 0m);
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

        // Rebuilds sfReport's dynamic columns (the per-rate Purchase@X%
        // columns, plus Tax/Others/Total) to match the current _rateKeys.
        // The 5 supplier-detail columns declared in XAML are never touched.
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

            AddSimple(nameof(PurchaseReportRow.Tax), "Tax", 90);
            AddSimple(nameof(PurchaseReportRow.Others), "Others", 90);
            AddSimple(nameof(PurchaseReportRow.Total), "Total", 120);
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

                var summaries = _purchaseRepo.GetReport(fromDate, toDate, txtVendorSearch.Text);
                var breakup = _purchaseRepo.GetTaxRateBreakup(fromDate, toDate, txtVendorSearch.Text);

                // Pivot the flat (PurchaseNo, TaxRate, TaxableAmount) rows
                // into a per-bill lookup, and collect the full set of rate
                // keys seen anywhere in this report (already excludes rates
                // that net to 0 — GetTaxRateBreakup filters those at the SQL
                // level).
                var byBill = new Dictionary<int, Dictionary<string, double>>();
                var rateKeySet = new SortedSet<double>();
                foreach (var b in breakup)
                {
                    string rateKey = b.TaxRate.ToString("0.##", CultureInfo.InvariantCulture);
                    rateKeySet.Add(b.TaxRate);

                    if (!byBill.TryGetValue(b.PurchaseNo, out var rateDict))
                        byBill[b.PurchaseNo] = rateDict = new Dictionary<string, double>();
                    rateDict[rateKey] = b.TaxableAmount;
                }
                _rateKeys = rateKeySet.Select(r => r.ToString("0.##", CultureInfo.InvariantCulture)).ToList();

                _rows.Clear();
                foreach (var s in summaries)
                {
                    var row = PurchaseReportRow.FromSummary(s);
                    if (byBill.TryGetValue(s.PurchaseNo, out var rateDict))
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

                lblTotalAmount.Text = _rows.Sum(r => r.Amount).ToString("N2");
                lblTotalTax.Text = _rows.Sum(r => r.Tax).ToString("N2");
                lblTotalOthers.Text = _rows.Sum(r => r.Others).ToString("N2");
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