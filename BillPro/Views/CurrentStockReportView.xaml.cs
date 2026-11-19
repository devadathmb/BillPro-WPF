using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Syncfusion.UI.Xaml.Grid;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Current Stock Report — Master-Details view, matching
    // SalesReturnItemReportView/PurchaseItemReportView's pattern (see those
    // files for the rationale behind the DockPanel-level mouse-event
    // wiring). The outer grid shows one row per item (Total Qty/Amount
    // summed across every batch); expanding a row reveals that item's
    // individual batches — MRP, rates, and per-batch current stock.
    //
    // Double-click / right-click "View Transaction History" on an expanded
    // batch row jumps to Individual Stock Report for that exact batch, via
    // EntryEditNavigator's EntryChannels.IndividualStockReport channel.
    //
    // PRINT/PDF/EXCEL — now expand-aware, matching SalesReturnItemReportView/
    // PurchaseItemReportView's two-mode + per-item behavior:
    //   - Nothing expanded: exports the plain item-wise summary (one row per
    //     item, same as the on-screen collapsed grid).
    //   - Anything expanded: exports full batch-level detail. Items that are
    //     currently expanded export every batch line plus a bold "— TOTAL —"
    //     row; items still collapsed export a single bold rolled-up row
    //     instead (MRP/Sales Rate/Purchase Rate blanked on both kinds of
    //     summary rows via CurrentStockReportRow.IsReportFieldBlank — those
    //     fields don't have one meaningful value once multiple batches are
    //     combined).
    public partial class CurrentStockReportView : UserControl
    {
        private readonly StockMasterRepository _stockRepo;
        private readonly ItemMasterRepository _itemRepo;

        private List<CurrentStockReportRow> _rows = new();
        private ObservableCollection<CurrentStockItemSummaryRow> _summaryRows = new();
        private readonly HashSet<string> _expandedItemCodes = new();

        private bool _initialized;

        public CurrentStockReportView()
        {
            InitializeComponent();

            _stockRepo = new StockMasterRepository();
            _stockRepo.InitializeDatabase();

            _itemRepo = new ItemMasterRepository();
            _itemRepo.InitializeDatabase();

            sfReport.ItemsSource = _summaryRows;

            dockReport.PreviewMouseDown += DockReport_PreviewMouseDown;
            dockReport.PreviewMouseRightButtonUp += DockReport_PreviewMouseRightButtonUp;

            sfReport.DetailsViewExpanded += SfReport_DetailsViewExpanded;
            sfReport.DetailsViewCollapsed += SfReport_DetailsViewCollapsed;

            LoadFilterLookups();

            dpAsOfDate.SelectedDate = DateTime.Today;
            _initialized = true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(LoadReport));
        }

        // ════════════════════════════════════════════════════════════════════
        // FILTER PANEL
        // ════════════════════════════════════════════════════════════════════
        private void LoadFilterLookups()
        {
            var companies = _itemRepo.GetCompanies().ToList();
            companies.Insert(0, new CompanyItem { CompanyCode = null, CompanyName = "(All Companies)" });
            cmbCompany.ItemsSource = companies;
            cmbCompany.SelectedIndex = 0;

            var categories = _itemRepo.GetCategories().ToList();
            categories.Insert(0, new CategoryItem { CategoryCode = null, CategoryName = "(All Categories)" });
            cmbCategory.ItemsSource = categories;
            cmbCategory.SelectedIndex = 0;
        }

        // Fired by the date picker and both combo boxes — re-runs the report
        // immediately on any filter change so Display is a convenience, not
        // a requirement.
        private void Filter_Changed(object sender, EventArgs e)
        {
            if (!_initialized) return;
            LoadReport();
        }

        private void TxtItemSearch_KeyDown(object sender, KeyEventArgs e)
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
            if (e.Record is CurrentStockItemSummaryRow row)
                _expandedItemCodes.Add(row.ItemCode);
        }

        private void SfReport_DetailsViewCollapsed(object sender, GridDetailsViewCollapsedEventArgs e)
        {
            if (e.Record is CurrentStockItemSummaryRow row)
                _expandedItemCodes.Remove(row.ItemCode);
        }

        // ════════════════════════════════════════════════════════════════════
        // DOUBLE-CLICK / RIGHT-CLICK ON EXPANDED (BATCH) ROWS
        // ════════════════════════════════════════════════════════════════════
        private void DockReport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2)
                return;

            var source = e.OriginalSource as DependencyObject;

            if (TryFindAncestorDataContext<CurrentStockReportRow>(source, out var batchRow))
            {
                OpenIndividualReport(batchRow);
                e.Handled = true;
                return;
            }

            if (TryFindAncestorDataContext<CurrentStockItemSummaryRow>(source, out var summaryRow))
            {
                ToggleSummaryRowExpansion(summaryRow);
                e.Handled = true;
            }
        }

        private void ToggleSummaryRowExpansion(CurrentStockItemSummaryRow summaryRow)
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
            if (!TryFindAncestorDataContext<CurrentStockReportRow>(e.OriginalSource as DependencyObject, out var row))
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

        private ContextMenu BuildDetailContextMenu(CurrentStockReportRow row)
        {
            var menu = new ContextMenu { Tag = row };

            var viewItem = new MenuItem { Header = "View Transaction History" };
            viewItem.Click += (s, e) => OpenIndividualReport(row);
            menu.Items.Add(viewItem);

            menu.Items.Add(new Separator());

            var copyItem = new MenuItem { Header = "Copy Item Code" };
            copyItem.Click += (s, e) => Clipboard.SetText(row.ItemCode ?? string.Empty);
            menu.Items.Add(copyItem);

            return menu;
        }

        private void OpenIndividualReport(CurrentStockReportRow row)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Individual Stock Report");
            EntryEditNavigator.RequestEdit(EntryChannels.IndividualStockReport, (row.ItemCode, (int?)row.BatchId));
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "CURRENT STOCK REPORT";
        private const string TotalRowLabel = "— TOTAL —";
        private const string CollapsedRowLabel = "— TOTAL —";

        // Full batch-level detail columns — used once anything is expanded.
        // Item Name and Batch No are included here (not just on the outer
        // summary row) so an exported/printed detail page reads correctly on
        // its own, same as the on-screen nested grid now shows them too.
        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Code",          nameof(CurrentStockReportRow.ItemCode),      width: 65),
                new ReportColumn("Item Name",     nameof(CurrentStockReportRow.ItemName),      width: 170),
                new ReportColumn("Batch No",      nameof(CurrentStockReportRow.BatchNumber),   width: 95),
                new ReportColumn("Company",       nameof(CurrentStockReportRow.CompanyName),   width: 95),
                new ReportColumn("Category",      nameof(CurrentStockReportRow.CategoryName),  width: 90),
                new ReportColumn("MRP",           nameof(CurrentStockReportRow.Mrp),           width: 70,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Sales Rate",    nameof(CurrentStockReportRow.SalesRate),     width: 80,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Purchase Rate", nameof(CurrentStockReportRow.PurchaseRate),  width: 85,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Current Qty",   nameof(CurrentStockReportRow.CurrentQty),    width: 85,  format: "N3", alignment: TextAlignment.Right),
                new ReportColumn("Amount",        nameof(CurrentStockReportRow.Amount),        width: 100, format: "N2", alignment: TextAlignment.Right),
            };
        }

        // Item-wise summary columns — used only when nothing on screen is
        // expanded, same shape as the collapsed on-screen grid.
        private List<ReportColumn> BuildSummaryOnlyColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Code",       nameof(CurrentStockItemSummaryRow.ItemCode),        width: 70),
                new ReportColumn("Item Name",  nameof(CurrentStockItemSummaryRow.ItemName),        width: 220),
                new ReportColumn("Company",    nameof(CurrentStockItemSummaryRow.CompanyName),     width: 110),
                new ReportColumn("Category",   nameof(CurrentStockItemSummaryRow.CategoryName),    width: 100),
                new ReportColumn("Total Qty",  nameof(CurrentStockItemSummaryRow.TotalCurrentQty), width: 90,  format: "N3", alignment: TextAlignment.Right),
                new ReportColumn("Total Amount", nameof(CurrentStockItemSummaryRow.TotalAmount),   width: 110, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle()
        {
            var parts = new List<string> { $"As of {dpAsOfDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}" };

            if (cmbCompany.SelectedValue is string companyCode && companyCode != null)
                parts.Add($"Company: {(cmbCompany.SelectedItem as CompanyItem)?.CompanyName}");

            if (cmbCategory.SelectedValue is string categoryCode && categoryCode != null)
                parts.Add($"Category: {(cmbCategory.SelectedItem as CategoryItem)?.CategoryName}");

            if (!string.IsNullOrWhiteSpace(txtItemSearch.Text))
                parts.Add($"Item Search: \"{txtItemSearch.Text}\"");

            return string.Join("  |  ", parts);
        }

        private string[] BuildFooterLines() => new[]
        {
            $"Total Qty: {_rows.Sum(r => r.CurrentQty):N3}",
            $"Total Stock Value: {_rows.Sum(r => r.Amount):N2}"
        };

        // Two-mode export, matching SalesReturnItemReportView/PurchaseItemReportView:
        //  - Nothing expanded: plain item-wise summary (BuildSummaryOnlyColumns).
        //  - Anything expanded: full batch detail (BuildReportColumns), with
        //    per-item collapse-awareness handled inside BuildExportRows.
        private (List<ReportColumn> Columns, System.Collections.IList Rows) BuildExportData()
        {
            if (_expandedItemCodes.Count == 0)
            {
                var summaryRows = _summaryRows.OrderBy(s => s.ItemName).ToList();
                return (BuildSummaryOnlyColumns(), summaryRows);
            }

            return (BuildReportColumns(), BuildExportRows());
        }

        // Builds the detail-mode export list: for each item, either its
        // individual batch lines + a bold "— TOTAL —" row (if that item is
        // currently expanded on screen) or a single bold rolled-up row
        // standing in for it (if still collapsed) — mirrors
        // PurchaseItemReportView's mixed-mode export. MRP/Sales Rate/Purchase
        // Rate are left blank on both kinds of summary rows via
        // CurrentStockReportRow.IsReportFieldBlank, since none of those
        // fields has one meaningful value once multiple batches (each
        // potentially at a different rate) are combined into one line.
        private List<CurrentStockReportRow> BuildExportRows()
        {
            var export = new List<CurrentStockReportRow>();

            foreach (var group in _rows.GroupBy(r => r.ItemCode)
                                        .OrderBy(g => g.First().ItemName))
            {
                var lines = group.OrderBy(r => r.Mrp).ToList();
                bool isExpanded = _expandedItemCodes.Contains(group.Key);

                if (isExpanded)
                    export.AddRange(lines);

                export.Add(new CurrentStockReportRow
                {
                    IsSummaryRow = true,
                    ItemCode = lines[0].ItemCode,
                    ItemName = lines[0].ItemName,
                    CompanyName = lines[0].CompanyName,
                    CategoryName = lines[0].CategoryName,
                    BatchNumber = isExpanded ? TotalRowLabel : CollapsedRowLabel,
                    CurrentQty = lines.Sum(r => r.CurrentQty),
                    AmountOverride = lines.Sum(r => r.Amount)
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
                DateTime asOfDate = dpAsOfDate.SelectedDate ?? DateTime.Today;
                string companyCode = cmbCompany.SelectedValue as string;
                string categoryCode = cmbCategory.SelectedValue as string;

                _rows = _stockRepo.GetCurrentStockReport(
                    asOfDate: asOfDate,
                    companyCode: companyCode,
                    categoryCode: categoryCode,
                    itemSearch: txtItemSearch.Text).ToList();

                // See SalesReturnItemReportView.LoadReport for why this must
                // be cleared alongside rebuilding _summaryRows.
                _expandedItemCodes.Clear();

                var summaries = CurrentStockItemSummaryRow.BuildFrom(_rows);

                // Hide-zero-stock is an item-level filter: an item whose
                // batches (or lack thereof — brand-new items get a single
                // synthetic zero-qty row) net to exactly zero is dropped
                // from view when the checkbox is on. Unchecking it brings
                // those items back, including ones that have never had a
                // single purchase/production/sale.
                if (chkHideZeroStock.IsChecked == true)
                    summaries = summaries.Where(s => s.TotalCurrentQty != 0).ToList();

                _summaryRows.Clear();
                foreach (var s in summaries)
                    _summaryRows.Add(s);

                // Totals live in the plain footer panel below the grid, same
                // as before — summed across every batch regardless of which
                // items are currently expanded.
                lblGrandTotalQty.Text = $"Total Qty: {_rows.Sum(r => r.CurrentQty):N3}";
                lblGrandTotalAmount.Text = $"Total Stock Value: {_rows.Sum(r => r.Amount):N2}";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}