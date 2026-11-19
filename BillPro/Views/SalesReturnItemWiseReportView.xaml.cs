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
    // Item-wise Sales Return Report — Master-Details view, mirrors
    // SalesItemReportView/PurchaseItemReportView exactly (see those files for
    // the rationale behind the DockPanel-level mouse-event wiring). The
    // outer grid shows one row per item; expanding a row reveals its
    // individual returned lines. There is no series/prefix here — the edit
    // navigation channel takes a plain int entry no (EntryChannels.SalesReturnEntry),
    // same as Purchase Entry's channel.
    public partial class SalesReturnItemReportView : UserControl
    {
        private readonly SalesReturnRepository _returnRepo;

        private List<SalesReturnItemReportRow> _rows = new();
        private ObservableCollection<SalesReturnItemSummaryRow> _summaryRows = new();
        private readonly HashSet<string> _expandedItemCodes = new();

        private bool _initialized;
        private bool _hasLoadedOnce;

        public SalesReturnItemReportView()
        {
            InitializeComponent();

            _returnRepo = new SalesReturnRepository();
            _returnRepo.InitializeDatabase();

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
            if (e.Record is SalesReturnItemSummaryRow row)
                _expandedItemCodes.Add(row.ItemCode);
        }

        private void SfReport_DetailsViewCollapsed(object sender, GridDetailsViewCollapsedEventArgs e)
        {
            if (e.Record is SalesReturnItemSummaryRow row)
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

            if (TryFindAncestorDataContext<SalesReturnItemReportRow>(source, out var detailRow))
            {
                OpenEditForEntry(detailRow.EntryNo);
                e.Handled = true;
                return;
            }

            if (TryFindAncestorDataContext<SalesReturnItemSummaryRow>(source, out var summaryRow))
            {
                ToggleSummaryRowExpansion(summaryRow);
                e.Handled = true;
            }
        }

        private void ToggleSummaryRowExpansion(SalesReturnItemSummaryRow summaryRow)
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
            if (!TryFindAncestorDataContext<SalesReturnItemReportRow>(e.OriginalSource as DependencyObject, out var row))
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

        private ContextMenu BuildDetailContextMenu(SalesReturnItemReportRow row)
        {
            var menu = new ContextMenu { Tag = row };

            var editItem = new MenuItem { Header = "Edit This Return" };
            editItem.Click += (s, e) => OpenEditForEntry(row.EntryNo);
            menu.Items.Add(editItem);

            var viewItem = new MenuItem { Header = "View Details" };
            viewItem.Click += (s, e) => ShowDetails(row);
            menu.Items.Add(viewItem);

            menu.Items.Add(new Separator());

            var copyItem = new MenuItem { Header = "Copy Entry No" };
            copyItem.Click += (s, e) => Clipboard.SetText(row.FormattedEntryNo ?? string.Empty);
            menu.Items.Add(copyItem);

            return menu;
        }

        private void ShowDetails(SalesReturnItemReportRow row)
        {
            try
            {
                var summary = _returnRepo.GetSummary(row.EntryNo);
                if (summary == null)
                {
                    MessageBox.Show("This return entry could not be found (it may have been deleted).",
                        "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var items = _returnRepo.GetItems(row.EntryNo);

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

        private void OpenEditForEntry(int entryNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Sales Return Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.SalesReturnEntry, entryNo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel.
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "ITEM-WISE SALES RETURN REPORT";
        private const string TotalRowLabel = "— TOTAL —";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Entry Date", nameof(SalesReturnItemReportRow.EntryDate),        width: 80),
                new ReportColumn("Entry No",   nameof(SalesReturnItemReportRow.FormattedEntryNo),  width: 90),
                new ReportColumn("Customer",   nameof(SalesReturnItemReportRow.CustomerName),      width: 150),
                new ReportColumn("Item Code",  nameof(SalesReturnItemReportRow.ItemCode),          width: 65),
                new ReportColumn("Item Name",  nameof(SalesReturnItemReportRow.ItemName),          width: 140),
                new ReportColumn("Rate",       nameof(SalesReturnItemReportRow.Rate),              width: 70,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Qty",        nameof(SalesReturnItemReportRow.Quantity),          width: 60,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Discount",   nameof(SalesReturnItemReportRow.Discount),          width: 75,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Tax Amt",    nameof(SalesReturnItemReportRow.TaxAmount),         width: 85,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Total",      nameof(SalesReturnItemReportRow.Total),             width: 150, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private List<ReportColumn> BuildSummaryOnlyColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Item Code", nameof(SalesReturnItemSummaryRow.ItemCode),      width: 90),
                new ReportColumn("Item Name", nameof(SalesReturnItemSummaryRow.ItemName),      width: 220),
                new ReportColumn("HSN",       nameof(SalesReturnItemSummaryRow.HsnCode),       width: 90),
                new ReportColumn("Qty",       nameof(SalesReturnItemSummaryRow.TotalQuantity), width: 80,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Discount",  nameof(SalesReturnItemSummaryRow.TotalDiscount), width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Tax Amt",   nameof(SalesReturnItemSummaryRow.TotalTaxAmount),width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Price",     nameof(SalesReturnItemSummaryRow.TotalPrice),    width: 100, format: "N2", alignment: TextAlignment.Right),
            };
        }

        private string BuildSubTitle()
        {
            var parts = new List<string>
            {
                $"{dpFromDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy} to {dpToDate.SelectedDate ?? DateTime.Today:dd-MMM-yyyy}"
            };

            if (!string.IsNullOrWhiteSpace(txtCustomerSearch.Text))
                parts.Add($"Search: \"{txtCustomerSearch.Text}\"");

            return string.Join("  |  ", parts);
        }

        private string[] BuildFooterLines() => new[]
        {
            $"Qty: {_rows.Sum(r => r.Quantity):N2}",
            $"Tax Amt: {_rows.Sum(r => r.TaxAmount):N2}",
            $"Total Price: {_rows.Sum(r => r.Total):N2}"
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

        private List<SalesReturnItemReportRow> BuildExportRows()
        {
            var export = new List<SalesReturnItemReportRow>();

            foreach (var group in _rows.GroupBy(r => r.ItemCode)
                                        .OrderBy(g => g.First().ItemName))
            {
                var lines = group.OrderByDescending(r => r.EntryDate).ToList();
                bool isExpanded = _expandedItemCodes.Contains(group.Key);

                if (isExpanded)
                    export.AddRange(lines);

                export.Add(new SalesReturnItemReportRow
                {
                    IsSummaryRow = true,
                    FormattedEntryNo = TotalRowLabel,
                    ItemCode = lines[0].ItemCode,
                    ItemName = lines[0].ItemName,
                    Quantity = lines.Sum(r => r.Quantity),
                    Discount = lines.Sum(r => r.Discount),
                    TaxAmount = lines.Sum(r => r.TaxAmount),
                    Total = lines.Sum(r => r.Total)
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

                _rows = _returnRepo.GetItemWiseReport(fromDate, toDate, txtCustomerSearch.Text).ToList();

                // See PurchaseItemReportView.LoadReport for why this must be
                // cleared alongside rebuilding _summaryRows.
                _expandedItemCodes.Clear();

                _summaryRows.Clear();
                foreach (var s in SalesReturnItemSummaryRow.BuildFrom(_rows))
                    _summaryRows.Add(s);

                lblTotalQty.Text = _rows.Sum(r => r.Quantity).ToString("N2");
                lblTotalTax.Text = _rows.Sum(r => r.TaxAmount).ToString("N2");
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