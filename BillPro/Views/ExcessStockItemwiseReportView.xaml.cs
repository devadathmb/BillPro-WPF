using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Item-wise Excess Stock Report — Master-Details view, mirrors
    // SalesReturnItemReportView (see that file for the rationale behind the
    // DockPanel-level mouse-event wiring). Dedicated to ExcessStockRepository
    // only — no type filter, since this report never mixes Short rows in.
    public partial class ExcessStockItemReportView : UserControl
    {
        private readonly ExcessStockRepository _repository;

        private List<StockAdjustmentItemReportRow> _rows = new();
        private ObservableCollection<StockAdjustmentItemSummaryRow> _summaryRows = new();
        private readonly HashSet<string> _expandedItemCodes = new();

        private bool _initialized;
        private bool _hasLoadedOnce;

        public ExcessStockItemReportView()
        {
            InitializeComponent();

            _repository = new ExcessStockRepository();
            _repository.InitializeDatabase();

            sfReport.ItemsSource = _summaryRows;

            dockReport.PreviewMouseDown += DockReport_PreviewMouseDown;
            dockReport.PreviewMouseRightButtonUp += DockReport_PreviewMouseRightButtonUp;

            sfReport.DetailsViewExpanded += SfReport_DetailsViewExpanded;
            sfReport.DetailsViewCollapsed += SfReport_DetailsViewCollapsed;

            var today = DateTime.Today;
            dpFromDate.SelectedDate = new DateTime(today.Year, today.Month, 1);
            dpToDate.SelectedDate = today;

            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_hasLoadedOnce) return;
            _hasLoadedOnce = true;
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

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                LoadReport();
                e.Handled = true;
            }
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e) => LoadReport();

        // ════════════════════════════════════════════════════════════════════
        // EXPAND / COLLAPSE ALL
        // ════════════════════════════════════════════════════════════════════
        private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
        {
            sfReport.ExpandAllDetailsView();
            _expandedItemCodes.Clear();
            foreach (var s in _summaryRows)
                _expandedItemCodes.Add(s.ItemCode);
        }

        private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            sfReport.CollapseAllDetailsView();
            _expandedItemCodes.Clear();
        }

        private void SfReport_DetailsViewExpanded(object sender, GridDetailsViewExpandedEventArgs e)
        {
            if (e.Record is StockAdjustmentItemSummaryRow row)
                _expandedItemCodes.Add(row.ItemCode);
        }

        private void SfReport_DetailsViewCollapsed(object sender, GridDetailsViewCollapsedEventArgs e)
        {
            if (e.Record is StockAdjustmentItemSummaryRow row)
                _expandedItemCodes.Remove(row.ItemCode);
        }

        // ════════════════════════════════════════════════════════════════════
        // DOUBLE-CLICK / RIGHT-CLICK ON EXPANDED (DETAIL) ROWS
        // ════════════════════════════════════════════════════════════════════
        private void DockReport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2)
                return;

            var source = e.OriginalSource as DependencyObject;

            if (TryFindAncestorDataContext<StockAdjustmentItemReportRow>(source, out var detailRow))
            {
                OpenEditForEntry(detailRow.EntryNo);
                e.Handled = true;
                return;
            }

            if (TryFindAncestorDataContext<StockAdjustmentItemSummaryRow>(source, out var summaryRow))
            {
                ToggleSummaryRowExpansion(summaryRow);
                e.Handled = true;
            }
        }

        private void ToggleSummaryRowExpansion(StockAdjustmentItemSummaryRow summaryRow)
        {
            int rowIndex = sfReport.ResolveToRowIndex(summaryRow);
            if (rowIndex < 0) return;
            int recordIndex = sfReport.ResolveToRecordIndex(rowIndex);

            if (_expandedItemCodes.Contains(summaryRow.ItemCode))
                sfReport.CollapseDetailsViewAt(recordIndex);
            else
                sfReport.ExpandDetailsViewAt(recordIndex);
        }

        private void DockReport_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!TryFindAncestorDataContext<StockAdjustmentItemReportRow>(e.OriginalSource as DependencyObject, out var row))
                return;

            var grid = FindAncestor<SfDataGrid>(e.OriginalSource as DependencyObject);
            if (grid != null)
                grid.SelectedItem = row;

            var menu = BuildDetailContextMenu(row);
            menu.PlacementTarget = sender as UIElement;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private bool TryFindAncestorDataContext<T>(DependencyObject source, out T row) where T : class
        {
            row = null;
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.DataContext is T match)
                {
                    row = match;
                    return true;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private T FindAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            var current = source;
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private ContextMenu BuildDetailContextMenu(StockAdjustmentItemReportRow row)
        {
            var menu = new ContextMenu { Tag = row };

            var editItem = new MenuItem { Header = "Edit This Excess Stock Entry" };
            editItem.Click += (s, e) => OpenEditForEntry(row.EntryNo);
            menu.Items.Add(editItem);

            menu.Items.Add(new Separator());

            var copyItem = new MenuItem { Header = "Copy Entry No" };
            copyItem.Click += (s, e) => Clipboard.SetText(row.FormattedEntryNo ?? string.Empty);
            menu.Items.Add(copyItem);

            return menu;
        }

        private void OpenEditForEntry(int entryNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Excess Stock Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.ExcessStockEntry, entryNo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel.
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "ITEM-WISE EXCESS STOCK REPORT";
        private const string TotalRowLabel = "— TOTAL —";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Entry Date", nameof(StockAdjustmentItemReportRow.EntryDate),        width: 80),
                new ReportColumn("Entry No",   nameof(StockAdjustmentItemReportRow.FormattedEntryNo),  width: 80),
                new ReportColumn("Item Code",  nameof(StockAdjustmentItemReportRow.ItemCode),          width: 65),
                new ReportColumn("Item Name",  nameof(StockAdjustmentItemReportRow.ItemName),          width: 140),
                new ReportColumn("MRP",        nameof(StockAdjustmentItemReportRow.Mrp),               width: 70,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Purch. Rate",nameof(StockAdjustmentItemReportRow.PurchaseRate),      width: 80,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Qty",        nameof(StockAdjustmentItemReportRow.Qty),               width: 70,  format: "N3", alignment: TextAlignment.Right),
            };
        }

        private List<ReportColumn> BuildSummaryOnlyColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Item Code", nameof(StockAdjustmentItemSummaryRow.ItemCode),          width: 90),
                new ReportColumn("Item Name", nameof(StockAdjustmentItemSummaryRow.ItemName),          width: 220),
                new ReportColumn("HSN",       nameof(StockAdjustmentItemSummaryRow.HsnCode),           width: 90),
                new ReportColumn("Qty",       nameof(StockAdjustmentItemSummaryRow.TotalQty),          width: 80,  format: "N3", alignment: TextAlignment.Right),
                new ReportColumn("Purch. Rate", nameof(StockAdjustmentItemSummaryRow.TotalPurchaseRate), width: 100, format: "N2", alignment: TextAlignment.Right),
            };
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

        private string[] BuildFooterLines() => new[]
        {
            $"Qty: {_rows.Sum(r => r.Qty):N3}",
            $"Purch. Rate: {_rows.Sum(r => r.PurchaseRate):N2}"
        };

        private (List<ReportColumn> Columns, System.Collections.IList Rows) BuildExportData()
        {
            if (_expandedItemCodes.Count == 0)
            {
                var summaryRows = _summaryRows.OrderBy(s => s.ItemName).ToList();
                return (BuildSummaryOnlyColumns(), summaryRows);
            }

            return (BuildReportColumns(), BuildExportRows());
        }

        private List<StockAdjustmentItemReportRow> BuildExportRows()
        {
            var export = new List<StockAdjustmentItemReportRow>();

            foreach (var group in _rows.GroupBy(r => r.ItemCode)
                                        .OrderBy(g => g.First().ItemName))
            {
                var lines = group.OrderByDescending(r => r.EntryDate).ToList();
                bool isExpanded = _expandedItemCodes.Contains(group.Key);

                if (isExpanded)
                    export.AddRange(lines);

                export.Add(new StockAdjustmentItemReportRow
                {
                    IsSummaryRow = true,
                    FormattedEntryNo = TotalRowLabel,
                    ItemCode = lines[0].ItemCode,
                    ItemName = lines[0].ItemName,
                    Qty = lines.Sum(r => r.Qty),
                    PurchaseRate = lines.Sum(r => r.PurchaseRate)
                });
            }

            return export;
        }

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("There is nothing to print. Load the report first.",
                    "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (columns, exportRows) = BuildExportData();

            ReportPrintHelper.Print(
                title: ReportTitle,
                columns: columns,
                rows: exportRows,
                footerLines: BuildFooterLines(),
                subTitle: BuildSubTitle(),
                pageMargin: new Thickness(40, 60, 28, 28),
                baseFontSize: 10.0);
        }

        private void BtnExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Load the report first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (columns, exportRows) = BuildExportData();

            ReportExcelHelper.Export(
                title: ReportTitle,
                columns: columns,
                rows: exportRows,
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

            var (columns, exportRows) = BuildExportData();

            ReportPdfHelper.Export(
                title: ReportTitle,
                columns: columns,
                rows: exportRows,
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

                _rows = _repository.GetItemWiseReport(fromDate, toDate, txtSearch.Text).ToList();

                // See SalesReturnItemReportView.LoadReport for why this must
                // be cleared alongside rebuilding _summaryRows.
                _expandedItemCodes.Clear();

                _summaryRows.Clear();
                foreach (var s in StockAdjustmentItemSummaryRow.BuildFrom(_rows))
                    _summaryRows.Add(s);

                lblTotalQty.Text = _rows.Sum(r => r.Qty).ToString("N3");
                lblGrandTotal.Text = _rows.Sum(r => r.PurchaseRate).ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}