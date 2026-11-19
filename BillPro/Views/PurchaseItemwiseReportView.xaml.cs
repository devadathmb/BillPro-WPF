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
    // Item-wise Purchase Report — Master-Details view. The outer grid
    // (sfReport) shows one row per item with additive totals only
    // (Qty/Discount/Tax/Price); expanding a row (via the nested
    // DetailsViewDefinition in XAML) reveals every individual purchase
    // line for that item with the full original column set, including
    // Rate/MRP which can differ bill to bill and so can't live on the
    // summary row. Editing/viewing a bill is only wired up on the nested
    // detail grid (SfDetailGrid_*, MenuEditBill_Click, etc.) since only
    // those rows are PurchaseItemReportRow with a PurchaseNo — the outer
    // summary rows are PurchaseItemSummaryRow and have no single bill to
    // edit.
    public partial class PurchaseItemReportView : UserControl
    {
        private readonly PurchaseRepository _purchaseRepo;

        // Flat list of every purchase-item line in the current filter —
        // kept only for footer totals and Print/PDF/Excel export, which
        // stay flat regardless of on-screen grouping.
        private List<PurchaseItemReportRow> _rows = new();

        // What sfReport is actually bound to: one row per item, each
        // carrying its own Details collection for the nested grid.
        private ObservableCollection<PurchaseItemSummaryRow> _summaryRows = new();

        // ItemCodes currently expanded on screen. Kept in sync via
        // DetailsViewExpanded/DetailsViewCollapsed (fired both for manual
        // +/- clicks and for our own programmatic expand/collapse below),
        // and drives Print/PDF/Excel export: only expanded items get their
        // full detail lines exported — see BuildExportRows().
        private readonly HashSet<string> _expandedItemCodes = new();

        private bool _initialized;

        // AvalonDock reuses the same view instance across tab switches
        // (OpenOrActivate below just sets existing.IsActive = true), but it
        // virtualizes inactive tab content — switching away and back
        // re-fires UserControl.Loaded on that SAME instance, even though
        // nothing was actually closed/reopened. Without this guard,
        // LoadReport() would rebuild _summaryRows from scratch on every
        // such reactivation (e.g. coming back from editing a bill),
        // collapsing every expanded row for no reason. Only the first
        // Loaded — the tab's genuine initial open — should trigger a load;
        // a real close+reopen creates a brand-new instance (fresh
        // factory() call in MainWindow), so this still loads correctly
        // then too. Filter changes / Display button continue to reload as
        // before, since those go through Filter_Changed/BtnDisplay_Click,
        // not this flag.
        private bool _hasLoadedOnce;

        public PurchaseItemReportView()
        {
            InitializeComponent();

            _purchaseRepo = new PurchaseRepository();
            _purchaseRepo.InitializeDatabase();

            sfReport.ItemsSource = _summaryRows;

            // Master-Details clones the DataGrid template defined in XAML for
            // every expanded row, but that clone doesn't reliably carry over
            // compiled event-handler wiring (Click, MouseDoubleClick), and the
            // exact "hand me the new nested grid instance" event differs across
            // Syncfusion SDK versions (GridDetailsViewExpandedEventArgs doesn't
            // even expose the same members everywhere). Rather than depend on
            // that fragile API surface, we attach handlers ONCE at the
            // DockPanel level and rely on plain WPF event bubbling: mouse
            // events fire on whatever nested grid/row was actually clicked and
            // bubble up through the visual tree regardless of how/when
            // Syncfusion created that nested grid. We then walk up from
            // e.OriginalSource to find the row's DataContext ourselves.
            //
            // NOTE: MouseDoubleClick/PreviewMouseDoubleClick only exist on
            // Control (that's why sfReport.MouseDoubleClick works in other
            // reports — SfDataGrid derives from Control). DockPanel is a
            // plain Panel, so double-click is detected manually here via
            // PreviewMouseDown + e.ClickCount == 2.
            dockReport.PreviewMouseDown += DockReport_PreviewMouseDown;
            dockReport.PreviewMouseRightButtonUp += DockReport_PreviewMouseRightButtonUp;

            // Keeps _expandedItemCodes in sync with the grid's actual expand
            // state — fires for the built-in +/- expander AND for our own
            // programmatic ExpandDetailsViewAt/CollapseDetailsViewAt calls
            // (see ToggleSummaryRowExpansion), so this is the single source
            // of truth regardless of how a row got expanded/collapsed.
            // e.Record is documented as the row's own data item (not a
            // wrapper) — cast straight to PurchaseItemSummaryRow.
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
        // EXPAND / COLLAPSE ALL — Master-Details view equivalents.
        // ════════════════════════════════════════════════════════════════════
        // ExpandAllDetailsView/CollapseAllDetailsView don't reliably raise
        // DetailsViewExpanded/DetailsViewCollapsed per row the way a manual
        // +/- click does, so _expandedItemCodes is updated directly here
        // rather than depending on those events for the bulk case.
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
            if (e.Record is PurchaseItemSummaryRow row)
                _expandedItemCodes.Add(row.ItemCode);
        }

        private void SfReport_DetailsViewCollapsed(object sender, GridDetailsViewCollapsedEventArgs e)
        {
            if (e.Record is PurchaseItemSummaryRow row)
                _expandedItemCodes.Remove(row.ItemCode);
        }

        // ════════════════════════════════════════════════════════════════════
        // DOUBLE-CLICK / RIGHT-CLICK ON EXPANDED (DETAIL) ROWS
        // Attached once at the DockPanel level in the constructor, so this
        // works no matter how/when Syncfusion creates the nested per-row
        // grids internally — we don't depend on any Master-Details-specific
        // event at all. Plain WPF mouse events bubble from wherever the user
        // actually clicked up through the visual tree, so we just walk up
        // from e.OriginalSource looking for a row whose DataContext is a
        // PurchaseItemReportRow (a detail-grid row). Summary rows are
        // PurchaseItemSummaryRow, so they're naturally excluded — matching
        // the requirement that editing/viewing is only reachable from the
        // expanded item list.
        //
        // Double-click is detected via PreviewMouseDown + e.ClickCount == 2
        // rather than a MouseDoubleClick event, since that event only exists
        // on Control (SfDataGrid has it; DockPanel, a plain Panel, doesn't).
        // ════════════════════════════════════════════════════════════════════
        private void DockReport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2)
                return;

            var source = e.OriginalSource as DependencyObject;

            if (TryFindAncestorDataContext<PurchaseItemReportRow>(source, out var detailRow))
            {
                OpenEditForBill(detailRow.PurchaseNo);
                e.Handled = true;
                return;
            }

            if (TryFindAncestorDataContext<PurchaseItemSummaryRow>(source, out var summaryRow))
            {
                ToggleSummaryRowExpansion(summaryRow);
                e.Handled = true;
            }
        }

        // Expands/collapses a summary row by data item rather than a raw
        // list index, since AllowSorting="True" on sfReport means the
        // on-screen row order can differ from _summaryRows' list order.
        // ResolveToRowIndex(object) is sort/filter-aware; ExpandDetailsViewAt
        // then wants that resolved further into a record index — this is
        // the standard two-step chain Syncfusion's own docs use for
        // expanding a specific row programmatically. If your installed SDK
        // names either method slightly differently, the compiler error will
        // point straight at it.
        private void ToggleSummaryRowExpansion(PurchaseItemSummaryRow summaryRow)
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
            if (!TryFindAncestorDataContext<PurchaseItemReportRow>(e.OriginalSource as DependencyObject, out var row))
                return;

            var grid = FindAncestor<SfDataGrid>(e.OriginalSource as DependencyObject);
            if (grid != null)
                grid.SelectedItem = row;

            var menu = BuildDetailContextMenu(row);
            menu.PlacementTarget = sender as UIElement;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // Walks up the visual tree from the clicked element looking for a
        // FrameworkElement whose DataContext matches the requested row
        // type. Used for both detail rows (PurchaseItemReportRow) and
        // summary rows (PurchaseItemSummaryRow) — whichever is asked for.
        // Stops as soon as it finds a match, or reaches the top (click
        // landed on the wrong row type, or outside any row entirely, e.g.
        // header/margin).
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

        private ContextMenu BuildDetailContextMenu(PurchaseItemReportRow row)
        {
            var menu = new ContextMenu { Tag = row };

            var editItem = new MenuItem { Header = "Edit This Bill" };
            editItem.Click += (s, e) => OpenEditForBill(row.PurchaseNo);
            menu.Items.Add(editItem);

            var viewItem = new MenuItem { Header = "View Details" };
            viewItem.Click += (s, e) => ShowDetails(row);
            menu.Items.Add(viewItem);

            menu.Items.Add(new Separator());

            var copyItem = new MenuItem { Header = "Copy Bill No" };
            copyItem.Click += (s, e) => Clipboard.SetText(row.BillNo ?? string.Empty);
            menu.Items.Add(copyItem);

            return menu;
        }

        private void ShowDetails(PurchaseItemReportRow row)
        {

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

        private void OpenEditForBill(int purchaseNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Purchase Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.PurchaseEntry, purchaseNo);
        }

        // ════════════════════════════════════════════════════════════════════
        // REPORT OUTPUT — shared by Print, PDF, and Excel.
        //
        // Two modes, chosen by current on-screen expand state:
        //  - Nothing expanded: a plain summary report (one row per item,
        //    same shape as the on-screen summary grid) — no bill-level
        //    columns at all, since there's nothing to break down.
        //  - Anything expanded: the full detail column set. Expanded items
        //    export their individual purchase lines plus a bold "— TOTAL —"
        //    row; items left collapsed export a single bold rolled-up row
        //    instead of their lines, with the columns that don't apply to
        //    an aggregate (MRP, rate) left blank rather than showing a
        //    misleading 0/default, and its "— TOTAL —" label spans the
        //    leading Date/Bill/Supplier columns as one merged, centered
        //    cell (see PurchaseItemReportRow.GetMergedLabelSpan) instead of
        //    those columns each showing their own value. Keeping one
        //    consistent column set in this mode — instead of switching
        //    per-item — is what makes it a single coherent report page.
        // ════════════════════════════════════════════════════════════════════
        private const string ReportTitle = "ITEM-WISE PURCHASE REPORT";
        private const string TotalRowLabel = "— TOTAL —";

        private List<ReportColumn> BuildReportColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Pur Date",  nameof(PurchaseItemReportRow.PurchaseDate), width: 80),
                new ReportColumn("Bill No",   nameof(PurchaseItemReportRow.BillNo),       width: 85),
                new ReportColumn("Supplier",  nameof(PurchaseItemReportRow.VendorName),   width: 150),
                new ReportColumn("Item Code", nameof(PurchaseItemReportRow.ItemCode),     width: 65),
                new ReportColumn("Item Name", nameof(PurchaseItemReportRow.ItemName),     width: 140),
                new ReportColumn("MRP",       nameof(PurchaseItemReportRow.Mrp),          width: 70,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Rate",      nameof(PurchaseItemReportRow.Rate),         width: 70,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Qty",       nameof(PurchaseItemReportRow.Quantity),     width: 60,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Discount",  nameof(PurchaseItemReportRow.Discount),     width: 75,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Tax Amt",   nameof(PurchaseItemReportRow.TaxAmount),    width: 85,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Price",     nameof(PurchaseItemReportRow.Price),        width: 150, format: "N2", alignment: TextAlignment.Right),
            };
        }

        // Used only when nothing on screen is expanded — same fields as the
        // outer summary grid (Qty/Discount/Tax/Price only; Rate/MRP are
        // deliberately excluded here too, same reason as the on-screen grid:
        // they can differ bill to bill and have no single value to show).
        private List<ReportColumn> BuildSummaryOnlyColumns()
        {
            return new List<ReportColumn>
            {
                new ReportColumn("Item Code", nameof(PurchaseItemSummaryRow.ItemCode),        width: 90),
                new ReportColumn("Item Name", nameof(PurchaseItemSummaryRow.ItemName),         width: 220),
                new ReportColumn("HSN",       nameof(PurchaseItemSummaryRow.HsnCode),          width: 90),
                new ReportColumn("Qty",       nameof(PurchaseItemSummaryRow.TotalQuantity),    width: 80,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Discount",  nameof(PurchaseItemSummaryRow.TotalDiscount),    width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Tax Amt",   nameof(PurchaseItemSummaryRow.TotalTaxAmount),   width: 90,  format: "N2", alignment: TextAlignment.Right),
                new ReportColumn("Price",     nameof(PurchaseItemSummaryRow.TotalPrice),       width: 100, format: "N2", alignment: TextAlignment.Right),
            };
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

        private string[] BuildFooterLines() => new[]
        {
            $"Qty: {_rows.Sum(r => r.Quantity):N2}",
            $"Tax Amt: {_rows.Sum(r => r.TaxAmount):N2}",
            $"Total Price: {_rows.Sum(r => r.Price):N2}"
        };

        // Picks columns + rows for the current export based on expand
        // state. Returns non-generic IList since Print/PDF/Excel all take
        // IEnumerable and reflect over property names at runtime — the two
        // possible row types (PurchaseItemSummaryRow vs PurchaseItemReportRow)
        // never mix within a single export, only the columns differ.
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
        // individual purchase lines + a bold total row (if expanded on
        // screen) or a single bold rolled-up row standing in for it (if
        // still collapsed) — see the mode comment above.
        private List<PurchaseItemReportRow> BuildExportRows()
        {
            var export = new List<PurchaseItemReportRow>();

            foreach (var group in _rows.GroupBy(r => r.ItemCode)
                                        .OrderBy(g => g.First().ItemName))
            {
                var lines = group.OrderByDescending(r => r.PurchaseDate).ToList();
                bool isExpanded = _expandedItemCodes.Contains(group.Key);

                if (isExpanded)
                    export.AddRange(lines);

                export.Add(new PurchaseItemReportRow
                {
                    IsSummaryRow = true,
                    BillNo = TotalRowLabel,
                    ItemCode = lines[0].ItemCode,
                    ItemName = lines[0].ItemName,
                    Quantity = lines.Sum(r => r.Quantity),
                    Discount = lines.Sum(r => r.Discount),
                    TaxAmount = lines.Sum(r => r.TaxAmount),
                    Price = lines.Sum(r => r.Price)
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

                _rows = _purchaseRepo.GetItemWiseReport(fromDate, toDate, txtVendorSearch.Text).ToList();

                _summaryRows.Clear();
                foreach (var s in PurchaseItemSummaryRow.BuildFrom(_rows))
                    _summaryRows.Add(s);

                lblTotalQty.Text = _rows.Sum(r => r.Quantity).ToString("N2");
                lblTotalTax.Text = _rows.Sum(r => r.TaxAmount).ToString("N2");
                lblGrandTotal.Text = _rows.Sum(r => r.Price).ToString("N2");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report:\n" + ex.Message,
                    "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}