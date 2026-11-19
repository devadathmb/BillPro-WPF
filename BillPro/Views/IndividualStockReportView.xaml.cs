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
    // STYLE SELECTOR (Must be public and outside the UserControl class)
    // ════════════════════════════════════════════════════════════════════════
    public class OpeningRowStyleSelector : StyleSelector
    {
        public Style OpeningRowStyle { get; set; }

        public override Style SelectStyle(object item, DependencyObject container)
        {
            // Applies the highlight style if the row is the synthetic opening balance row
            if (item is StockTransactionReportRow row &&
                row.TransType == IndividualStockReportView.OpeningBalanceTransType)
            {
                return OpeningRowStyle;
            }

            return base.SelectStyle(item, container);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // MAIN VIEW CLASS
    // ════════════════════════════════════════════════════════════════════════
    public partial class IndividualStockReportView : UserControl, IDisposable
    {
        private readonly StockMasterRepository _stockRepo;

        private List<ItemBatchPickerLookup> _allBatches = new();
        private List<StockTransactionReportRow> _allRowsForBatch = new(); // full history, unfiltered
        private ObservableCollection<StockTransactionReportRow> _displayRows = new();

        private int? _selectedBatchId;
        private bool _initialized;
        private bool _isUpdatingSource; // Prevents recursive selection events when swapping the ComboBox list

        // Marks the synthetic "Opening Balance" reference row's TransType.
        // MUST BE PUBLIC so the OpeningRowStyleSelector can see it.
        public const string OpeningBalanceTransType = "OPENING_BALANCE_REF";

        public IndividualStockReportView()
        {
            InitializeComponent();

            _stockRepo = new StockMasterRepository();
            _stockRepo.InitializeDatabase();

            sfReport.ItemsSource = _displayRows;

            modeFilter.LoadItems(TransactionModeOptions.All, allLabel: "All Types");

            LoadBatchPickerItems();

            var today = DateTime.Today;
            dpFromDate.SelectedDate = new DateTime(today.Year, today.Month, 1);
            dpToDate.SelectedDate = today;

            EntryEditNavigator.Subscribe<(string ItemCode, int? BatchId)>(
                EntryChannels.IndividualStockReport, OnShowItemRequested);

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // No auto-load here — unlike other reports, there's nothing
            // meaningful to display until an item/batch is actually picked
            // (either by the user or via the cross-report navigation below).
        }

        // ════════════════════════════════════════════════════════════════════
        // ITEM/BATCH PICKER (DYNAMIC GROUPING)
        // ════════════════════════════════════════════════════════════════════
        private void LoadBatchPickerItems()
        {
            _allBatches = _stockRepo.GetBatchPickerLookups();
            UpdateComboBoxSource(); // Render properly based on the checkbox state
        }

        private void UpdateComboBoxSource()
        {
            _isUpdatingSource = true;

            // Remember the currently selected item so we can re-select it after swapping lists
            string currentItemCode = (cmbItemBatch.SelectedItem as ItemBatchPickerLookup)?.ItemCode;

            // Hide-zero-stock is applied here, before grouping, so it works
            // the same way in both single-batch and combined-batches mode:
            // a batch with 0 stock disappears in single mode, and an item
            // whose every batch is 0 (or a brand-new item with no batch at
            // all — CurrentQty = 0 by construction) disappears in combined
            // mode too.
            var sourceBatches = chkHideZeroStock.IsChecked == true
                ? _allBatches.Where(b => b.CurrentQty != 0).ToList()
                : _allBatches;

            if (chkCombineBatches.IsChecked == true)
            {
                // Group by ItemCode so the user only sees one entry per item
                var uniqueItems = sourceBatches
                    .GroupBy(b => b.ItemCode)
                    .Select(g => new ItemBatchPickerLookup
                    {
                        ItemCode = g.Key,
                        ItemName = g.First().ItemName,
                        BatchId = g.First().BatchId, // Keep one valid BatchId so queries don't break
                        BatchNumber = "All Batches", // Visually clear to the user
                        Mrp = 0,                     // N/A for combined
                        CurrentQty = g.Sum(x => x.CurrentQty),
                        PurchaseRate = g.First().PurchaseRate
                    })
                    .OrderBy(i => i.ItemName)
                    .ToList();

                cmbItemBatch.ItemsSource = uniqueItems;
            }
            else
            {
                // Show every individual batch
                cmbItemBatch.ItemsSource = sourceBatches;
            }

            // Restore the user's previous selection if they had one
            if (currentItemCode != null)
            {
                var list = cmbItemBatch.ItemsSource as List<ItemBatchPickerLookup>;
                var match = list?.FirstOrDefault(b => b.ItemCode == currentItemCode);

                if (match != null)
                {
                    cmbItemBatch.SelectedItem = match;
                    _selectedBatchId = match.BatchId;
                }
            }

            _isUpdatingSource = false;
        }

        private void CmbItemBatch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore the event if we are currently swapping the lists in the background
            if (!_initialized || _isUpdatingSource) return;
            if (cmbItemBatch.SelectedItem is not ItemBatchPickerLookup selected) return;

            _selectedBatchId = selected.BatchId;
            LoadReport();
        }

        /// <summary>Re-runs the report against the currently selected item,
        /// switching between single-batch and combined-item history.</summary>
        private void ChkCombineBatches_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;

            // Re-group or un-group the dropdown list based on the new state
            UpdateComboBoxSource();

            if (_selectedBatchId != null)
            {
                LoadReport();
            }
        }

        /// <summary>Re-filters the dropdown list when the zero-stock checkbox
        /// is toggled. If the currently selected item drops out of the list
        /// (its stock is 0 and the box was just checked), the selection and
        /// loaded report are cleared rather than left pointing at a batch
        /// that's no longer visible.</summary>
        private void ChkHideZeroStock_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;

            UpdateComboBoxSource();

            if (cmbItemBatch.SelectedItem is ItemBatchPickerLookup selected)
            {
                _selectedBatchId = selected.BatchId;
                LoadReport();
            }
            else
            {
                _selectedBatchId = null;
                _allRowsForBatch.Clear();
                _displayRows.Clear();
                UpdateCurrentSnapshot();
            }
        }

        /// <summary>Fired via EntryEditNavigator when another report asks to jump here.</summary>
        private void OnShowItemRequested((string ItemCode, int? BatchId) request)
        {
            // Batch list may have changed since this view was constructed
            LoadBatchPickerItems();

            var currentList = cmbItemBatch.ItemsSource as List<ItemBatchPickerLookup>;
            if (currentList == null) return;

            ItemBatchPickerLookup match = null;

            // If we are in single-batch mode and a specific batch was requested
            if (request.BatchId.HasValue && chkCombineBatches.IsChecked == false)
            {
                match = currentList.FirstOrDefault(b => b.BatchId == request.BatchId.Value);
            }

            // Fallback: match by ItemCode 
            if (match == null)
            {
                match = currentList.FirstOrDefault(b => b.ItemCode == request.ItemCode);
            }

            if (match != null)
            {
                cmbItemBatch.SelectedItem = match;
                // SelectionChanged will automatically fire and call LoadReport()
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // FILTER PANEL
        // ════════════════════════════════════════════════════════════════════
        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized || _selectedBatchId == null) return;
            ApplyFilterToDisplay();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchId == null)
            {
                MessageBox.Show("Please select an item/batch first.",
                    "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            LoadReport();
        }

        // ════════════════════════════════════════════════════════════════════
        // ROW ACTIONS — jump to the source document
        // ════════════════════════════════════════════════════════════════════
        private void SfReport_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sfReport.SelectedItem is StockTransactionReportRow row)
                OpenSourceDocument(row);
        }

        private void MenuOpenSource_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is StockTransactionReportRow row)
                OpenSourceDocument(row);
        }

        private void MenuCopySource_Click(object sender, RoutedEventArgs e)
        {
            if (sfReport.SelectedItem is StockTransactionReportRow row && !string.IsNullOrWhiteSpace(row.SourceDisplayNo))
                Clipboard.SetText(row.SourceDisplayNo);
        }

        private void OpenSourceDocument(StockTransactionReportRow row)
        {
            // The synthetic Opening Balance row (TransId 0, no SourceType)
            // already fails row.HasSource below and gets the same friendly
            // "no linked source document" message as a legacy/manual ledger entry.
            if (!row.HasSource)
            {
                MessageBox.Show("This transaction has no linked source document (it may be a legacy or manually-adjusted entry).",
                    "No Source Document", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            switch (row.SourceType)
            {
                case "Purchase":
                    MainWindow.Instance?.OpenOrActivateTitle("Purchase Entry");
                    EntryEditNavigator.RequestEdit(EntryChannels.PurchaseEntry, row.PurchaseNo ?? 0);
                    break;

                case "Sales":
                    MainWindow.Instance?.OpenOrActivateTitle("Sales Entry");
                    EntryEditNavigator.RequestEdit(EntryChannels.SalesEntry, (row.SalesPrefix, row.SalesBillNo ?? 0));
                    break;

                case "Sales Return":
                    MainWindow.Instance?.OpenOrActivateTitle("Sales Return Entry");
                    EntryEditNavigator.RequestEdit(EntryChannels.SalesReturnEntry, row.ReturnEntryNo ?? 0);
                    break;

                case "Purchase Return":
                    MainWindow.Instance?.OpenOrActivateTitle("Purchase Return Entry");
                    EntryEditNavigator.RequestEdit(EntryChannels.PurchaseReturnEntry, row.ReturnEntryNo ?? 0);
                    break;

                case "Opening Stock":
                    MainWindow.Instance?.OpenOrActivateTitle("Opening Stock Entry");
                    EntryEditNavigator.RequestEdit(EntryChannels.OpeningStockEntry, row.OpeningEntryNo);
                    break;

                case "Excess Stock":
                    MainWindow.Instance?.OpenOrActivateTitle("Excess Stock Entry");
                    EntryEditNavigator.RequestEdit(EntryChannels.ExcessStockEntry, row.AdjustmentEntryNo ?? 0);
                    break;

                case "Short Stock":
                    MainWindow.Instance?.OpenOrActivateTitle("Short Stock Entry");
                    EntryEditNavigator.RequestEdit(EntryChannels.ShortStockEntry, row.AdjustmentEntryNo ?? 0);
                    break;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "INDIVIDUAL STOCK REPORT";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Date",     nameof(StockTransactionReportRow.TransDate),       width: 85),
                new ReportColumn("Type",     nameof(StockTransactionReportRow.TransTypeLabel),  width: 100),
                new ReportColumn("Batch",    nameof(StockTransactionReportRow.BatchNumber),     width: 90),
                new ReportColumn("MRP",      nameof(StockTransactionReportRow.Mrp),             width: 70,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Qty In",   nameof(StockTransactionReportRow.QtyIn),           width: 80,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Qty Out",  nameof(StockTransactionReportRow.QtyOut),          width: 80,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Balance",  nameof(StockTransactionReportRow.RunningBalance),  width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Source",   nameof(StockTransactionReportRow.SourceDisplayNo), width: 140),
            };
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>();

            if (cmbItemBatch.SelectedItem is ItemBatchPickerLookup selected)
            {
                parts.Add(chkCombineBatches.IsChecked == true
                    ? $"{selected.ItemName} ({selected.ItemCode}) — All Batches"
                    : $"{selected.ItemName} ({selected.ItemCode}) — Batch: {selected.BatchNumber ?? "—"}, MRP {selected.Mrp:N2}");
            }

            parts.Add($"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}");

            var types = modeFilter.SelectedValues;
            if (types.Count > 0)
                parts.Add($"Type: {string.Join(", ", types.Select(TransactionModeOptions.LabelFor))}");

            return string.Join("  |  ", parts);
        }

        private string[] BuildFooterLines() => new[]
        {
            $"Qty In: {_displayRows.Sum(r => r.QtyIn):N2}",
            $"Qty Out: {_displayRows.Sum(r => r.QtyOut):N2}",
            $"Closing Balance: {(_displayRows.Count > 0 ? _displayRows.Last().RunningBalance : 0):N2}"
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
            if (_selectedBatchId == null) return;
            if (cmbItemBatch.SelectedItem is not ItemBatchPickerLookup selected) return;

            try
            {
                // Full unfiltered history — running balance is computed over
                // this, then the date/type filters are applied on top for
                // display only (see ApplyFilterToDisplay). Which repository
                // call runs depends on the combine-batches toggle.
                _allRowsForBatch = chkCombineBatches.IsChecked == true
                    ? _stockRepo.GetTransactionHistoryForItem(selected.ItemCode)
                    : _stockRepo.GetTransactionHistory(_selectedBatchId.Value);

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
            var selectedTypes = modeFilter.SelectedValues;

            var filtered = _allRowsForBatch.Where(r =>
                r.TransDate.Date >= fromDate.Date && r.TransDate.Date <= toDate.Date &&
                (selectedTypes.Count == 0 || selectedTypes.Contains(r.TransType, StringComparer.OrdinalIgnoreCase)));

            _displayRows.Clear();

            // Always shown regardless of the Type filter
            _displayRows.Add(BuildOpeningBalanceRow(fromDate));

            foreach (var r in filtered)
                _displayRows.Add(r);

            UpdateTotalsBar();
        }

        /// <summary>
        /// Synthetic, non-persisted row representing the running balance as
        /// of the instant before <paramref name="fromDate"/>.
        /// </summary>
        private StockTransactionReportRow BuildOpeningBalanceRow(DateTime fromDate)
        {
            double openingBalance = _allRowsForBatch
                .Where(r => r.TransDate.Date < fromDate.Date)
                .Select(r => (double?)r.RunningBalance)
                .LastOrDefault() ?? 0;

            return new StockTransactionReportRow
            {
                TransId = 0,
                TransDate = fromDate,
                TransType = OpeningBalanceTransType,
                TransTypeLabel = "Opening Balance",
                QtyIn = 0,
                QtyOut = 0,
                RunningBalance = openingBalance
            };
        }

        private void UpdateTotalsBar()
        {
            lblTotalQtyIn.Text = _displayRows.Sum(r => r.QtyIn).ToString("N2");
            lblTotalQtyOut.Text = _displayRows.Sum(r => r.QtyOut).ToString("N2");
            lblClosingStock.Text = (_displayRows.Count > 0 ? _displayRows.Last().RunningBalance : 0).ToString("N2");
        }

        private void UpdateCurrentSnapshot()
        {
            if (cmbItemBatch.SelectedItem is not ItemBatchPickerLookup selected)
            {
                lblMrp.Text = "—";
                lblCurrentStock.Text = "—";
                lblCurrentValue.Text = "—";
                return;
            }

            double currentQty = _allRowsForBatch.Count > 0 ? _allRowsForBatch.Last().RunningBalance : 0;
            lblCurrentStock.Text = currentQty.ToString("N2");

            if (chkCombineBatches.IsChecked == true)
            {
                // A single MRP figure doesn't mean anything once multiple
                // batches are merged — say so plainly instead of showing
                // just whichever batch happened to be selected in the combo.
                lblMrp.Text = "Multiple";

                // Stock value across every batch of this item, using each
                // batch's own purchase rate rather than the selected batch's
                // rate alone — pulled from the already-loaded batch picker
                // list rather than a fresh query.
                double totalValue = _allBatches
                    .Where(b => b.ItemCode == selected.ItemCode)
                    .Sum(b => b.CurrentQty * b.PurchaseRate);
                lblCurrentValue.Text = totalValue.ToString("N2");
            }
            else
            {
                lblMrp.Text = selected.Mrp.ToString("N2");
                lblCurrentValue.Text = (currentQty * selected.PurchaseRate).ToString("N2");
            }
        }

        public void Dispose()
        {
            EntryEditNavigator.Unsubscribe<(string ItemCode, int? BatchId)>(
                EntryChannels.IndividualStockReport, OnShowItemRequested);
        }
    }
}