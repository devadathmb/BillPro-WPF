using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BillPro.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;
// Alias to resolve ambiguity with Syncfusion.UI.Xaml.Grid.SelectionChangedEventArgs
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace BillPro.Views
{
    public partial class OpeningStockEntryView : UserControl, IDisposable
    {
        private readonly OpeningStockRepository _repo;

        // Lookup data (loaded once at startup, refreshed on tab activation)
        public ObservableCollection<ItemLookup> AllItemsList { get; } = new();

        // Grid backing collection
        private ObservableCollection<OpeningStockLine> _lines = new();

        // State
        private string _saveMode = "Save";   // "Save" | "Update"
        private string _editingEntryNo = "";

        public OpeningStockEntryView()
        {
            InitializeComponent();

            this.IsVisibleChanged += OpeningStockEntryView_IsVisibleChanged;

            _repo = new OpeningStockRepository();
            _repo.InitializeDatabase();

            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;

            sfLines.ItemsSource = _lines;

            // Lets a report (or any other screen) ask this view to load a
            // specific opening-stock entry for editing via the generic
            // EntryEditNavigator bridge — same pattern as Sales/Purchase
            // Return Entry. EntryNo here is the plain string key already
            // used throughout OpeningStockRepository (opening_stock_master.
            // entry_no), NOT an int — unlike the return entries, this
            // module's entry number was never parsed as an integer anywhere
            // else in the codebase, so the channel is typed to match.
            // Subscribed once for the lifetime of this instance — see
            // Dispose() for why this is not undone on Unloaded.
            EntryEditNavigator.Subscribe<string>(EntryChannels.OpeningStockEntry, OnEditRequested);

            LoadLookups();
        }

        private void OpeningStockEntryView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
                RefreshMasterData();
        }

        private void RefreshMasterData()
        {
            try
            {
                var currentEntryItem = cmbEntryItemName.SelectedValue;

                var latestItems = _repo.GetItemLookups();

                AllItemsList.Clear();
                foreach (var item in latestItems)
                    AllItemsList.Add(item);

                if (currentEntryItem != null)
                    cmbEntryItemName.SelectedValue = currentEntryItem;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error refreshing data:\n" + ex.Message,
                    "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => btnNew.Focus()));
        }

        // ════════════════════════════════════════════════════════════════════
        // DATA LOADING
        // ════════════════════════════════════════════════════════════════════
        private void LoadLookups()
        {
            try
            {
                AllItemsList.Clear();
                foreach (var item in _repo.GetItemLookups())
                    AllItemsList.Add(item);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading lookup data:\n" + ex.Message,
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // UI STATE HELPERS
        // ════════════════════════════════════════════════════════════════════
        private void ToggleFields(bool enabled)
        {
            txtRemarks.IsEnabled = enabled;
            sfLines.IsEnabled = enabled;
            ToggleEntryLine(enabled);
            btnSave.IsEnabled = enabled;
        }

        private void ToggleEntryLine(bool enabled)
        {
            txtEntryItemCode.IsEnabled = enabled;
            cmbEntryItemName.IsEnabled = enabled;
            txtEntryMrp.IsEnabled = enabled;
            txtEntryQty.IsEnabled = enabled;
            txtEntryPurchaseRate.IsEnabled = enabled;
            txtEntrySalesRate.IsEnabled = enabled;
            btnAddItem.IsEnabled = enabled;
        }

        private void ClearAll()
        {
            dpEntryDate.SelectedDate = DateTime.Today;
            txtRemarks.Text = "";
            _lines.Clear();
            ClearEntryLine();
        }

        private void ClearEntryLine()
        {
            txtEntryItemCode.Text = "";

            cmbEntryItemName.SelectionChanged -= CmbEntryItemName_SelectionChanged;
            cmbEntryItemName.SelectedIndex = -1;
            cmbEntryItemName.Text = "";
            cmbEntryItemName.SelectionChanged += CmbEntryItemName_SelectionChanged;

            txtEntryMrp.Text = "";
            txtEntryQty.Text = "";
            txtEntryPurchaseRate.Text = "";
            txtEntrySalesRate.Text = "";
        }

        // ════════════════════════════════════════════════════════════════════
        // BUTTON HANDLERS
        // ════════════════════════════════════════════════════════════════════
        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            _saveMode = "Save";
            _editingEntryNo = "";

            ClearAll();
            ToggleFields(true);

            txtEntryNo.Text = _repo.GetNextEntryNo();
            btnSave.Content = "_Save";
            btnDelete.IsEnabled = false;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => txtEntryItemCode.Focus()));
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (dpEntryDate.SelectedDate == null)
            {
                MessageBox.Show("Please select an entry date.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                dpEntryDate.Focus();
                return;
            }

            var lines = _lines.Where(l => !string.IsNullOrWhiteSpace(l.ItemCode) && l.Qty > 0).ToList();
            if (!lines.Any())
            {
                MessageBox.Show("Please add at least one item line.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var header = new OpeningStockMaster
            {
                EntryNo = txtEntryNo.Text.Trim(),
                EntryDate = dpEntryDate.SelectedDate ?? DateTime.Today,
                Remarks = txtRemarks.Text.Trim()
            };

            try
            {
                if (_saveMode == "Save")
                {
                    _repo.SaveNew(header, lines, user: "Admin");
                    MessageBox.Show("Opening stock entry saved successfully.", "Saved",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    _repo.Update(header, lines, user: "Admin");
                    MessageBox.Show("Opening stock entry updated successfully.", "Updated",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _saveMode = "Save";
                _editingEntryNo = "";
                ClearAll();
                ToggleFields(false);
                btnSave.Content = "_Save";
                btnDelete.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving entry:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allHeaders = new List<OpeningStockMaster>();
            // GetAll is not on the repo yet — reuse GetHeader per entry as needed,
            // but for search purposes we scan entry numbers sequentially.
            // Simplest: expose a GetAllHeaders() on the repo. Falling back to a
            // direct search config bound to an on-demand list if unavailable.
            try
            {
                allHeaders = _repo.GetAllHeaders();
            }
            catch
            {
                MessageBox.Show("Unable to load entries for search.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var config = new SearchConfig
            {
                Title = "Search Opening Stock Entries",
                SearchProperties = new List<string> { "EntryNo", "Remarks" },
                ShowDateFilter = true,
                DateFilterProperty = "EntryDate",
                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "Entry No",   BindingPath = "EntryNo",   Width = "100" },
                    new SearchColumn { Header = "Entry Date", BindingPath = "EntryDate", Width = "150", StringFormat = "{0:dd/MM/yyyy}" },
                    new SearchColumn { Header = "Remarks",    BindingPath = "Remarks",   Width = "*" },
                },
                TargetOnSelect = btnSave
            };

            SearchOverlay.LoadData(allHeaders, config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is OpeningStockMaster selected)
                LoadEntryForEdit(selected.EntryNo);
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            btnDisplay.Focus();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (string.IsNullOrEmpty(_editingEntryNo)) return;

            if (MessageBox.Show("The selected opening stock entry will be permanently deleted,\n" +
                    "including all its ledger movements!\n\nAre you sure?",
                    "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    _repo.Delete(_editingEntryNo);

                    _saveMode = "Save";
                    _editingEntryNo = "";
                    ClearAll();
                    ToggleFields(false);
                    btnSave.IsEnabled = false;
                    btnDelete.IsEnabled = false;
                    MessageBox.Show("Entry deleted successfully.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    btnNew.Focus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error deleting entry:\n" + ex.Message, "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public void LoadEntryForEdit(string entryNo)
        {
            try
            {
                var header = _repo.GetHeader(entryNo);
                if (header == null) return;

                var lines = _repo.GetLines(entryNo);

                ToggleFields(true);
                _saveMode = "Update";
                _editingEntryNo = entryNo;

                txtEntryNo.Text = header.EntryNo;
                dpEntryDate.SelectedDate = header.EntryDate;
                txtRemarks.Text = header.Remarks;

                _lines.Clear();
                foreach (var line in lines)
                    _lines.Add(line);

                ClearEntryLine();

                btnSave.Content = "_Update";
                btnSave.IsEnabled = true;
                btnDelete.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading entry:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // EDIT NAVIGATION (EntryEditNavigator bridge)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles an edit request raised on the OpeningStockEntry channel.
        /// No unsaved-work confirmation here (unlike Sales/Purchase Entry) —
        /// this view has no such guard on New/Display either today, so this
        /// stays consistent with the rest of the screen rather than adding
        /// asymmetric behavior. Add one later if that changes.
        /// </summary>
        private void OnEditRequested(string entryNo)
        {
            if (_saveMode == "Update" && _editingEntryNo == entryNo)
                return; // already showing this entry

            LoadEntryForEdit(entryNo);
        }

        /// <summary>Called by MainWindow when this view's tab is actually
        /// closed (LayoutDocument.Closed) — see EntryEditNavigator's remarks
        /// on why this must NOT happen on UserControl.Unloaded instead.</summary>
        public void Dispose()
        {
            EntryEditNavigator.Unsubscribe<string>(EntryChannels.OpeningStockEntry, OnEditRequested);
        }

        // ════════════════════════════════════════════════════════════════════
        // ITEM CODE / NAME LOOKUP
        // ════════════════════════════════════════════════════════════════════
        private void TxtEntryItemCode_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                LookupAndFillItemByCode(txtEntryItemCode.Text.Trim());
                e.Handled = true;
            }
        }

        private void TxtEntryItemCode_LostFocus(object sender, RoutedEventArgs e)
        {
            string code = txtEntryItemCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(code)) return;

            if (cmbEntryItemName.SelectedItem is ItemLookup already
                && string.Equals(already.ItemCode, code, StringComparison.OrdinalIgnoreCase))
                return;

            LookupAndFillItemByCode(code);
        }

        private void LookupAndFillItemByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            var found = AllItemsList.FirstOrDefault(i =>
                string.Equals(i.ItemCode, code, StringComparison.OrdinalIgnoreCase));

            if (found != null)
            {
                cmbEntryItemName.SelectionChanged -= CmbEntryItemName_SelectionChanged;
                cmbEntryItemName.SelectedItem = found;
                cmbEntryItemName.SelectionChanged += CmbEntryItemName_SelectionChanged;

                FillEntryLine(found);
                Dispatcher.BeginInvoke(new Action(() => txtEntryQty.Focus()), DispatcherPriority.Input);
            }
            else
            {
                MessageBox.Show($"Item code \"{code}\" was not found.",
                    "Item Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryItemCode.Clear();
                Dispatcher.BeginInvoke(new Action(() => txtEntryItemCode.Focus()), DispatcherPriority.Input);
            }
        }

        private void CmbEntryItemName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemLookup selected) return;

            FillEntryLine(selected);
            Dispatcher.BeginInvoke(new Action(() => txtEntryMrp.Focus()), DispatcherPriority.Input);
        }

        private void FillEntryLine(ItemLookup item)
        {
            txtEntryItemCode.Text = item.ItemCode;
            txtEntryMrp.Text = item.Mrp.ToString("N2");
            txtEntryPurchaseRate.Text = item.UnitPricePurchase.ToString("N2");
            txtEntrySalesRate.Text = item.UnitPriceSales.ToString("N2");
            txtEntryQty.Text = "";
        }

        // ════════════════════════════════════════════════════════════════════
        // ADD ITEM LINE  (with soft duplicate-batch warning)
        // ════════════════════════════════════════════════════════════════════
        private void BtnAddItem_Click(object sender, RoutedEventArgs e) => CommitEntryLine();

        private void CommitEntryLine()
        {
            if (cmbEntryItemName.SelectedItem is not ItemLookup selected)
            {
                MessageBox.Show("Please select a valid item from the list.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                cmbEntryItemName.Focus();
                return;
            }

            if (!decimal.TryParse(txtEntryMrp.Text, out decimal mrp) || mrp < 0)
            {
                MessageBox.Show("Please enter a valid MRP.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryMrp.Focus();
                return;
            }

            if (!decimal.TryParse(txtEntryQty.Text, out decimal qty) || qty <= 0)
            {
                MessageBox.Show("Please enter a valid quantity greater than zero.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryQty.Focus();
                return;
            }

            if (!decimal.TryParse(txtEntryPurchaseRate.Text, out decimal purchaseRate) || purchaseRate < 0)
            {
                MessageBox.Show("Please enter a valid purchase rate.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryPurchaseRate.Focus();
                return;
            }

            decimal.TryParse(txtEntrySalesRate.Text, out decimal salesRate);

            // Soft warning: item+MRP batch already has stock recorded.
            // Informational only — legitimate reasons exist (correction, staggered entry).
            double existingQty = _repo.GetCurrentQtyForItemMrp(selected.ItemCode, mrp);
            if (existingQty != 0)
            {
                var ans = MessageBox.Show(
                    $"\"{selected.ItemName}\" @ MRP {mrp:N2} already has {existingQty:N2} units recorded.\n\n" +
                    "Continue and add this as an additional line?",
                    "Existing Stock Found", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (ans == MessageBoxResult.No)
                {
                    txtEntryQty.Focus();
                    return;
                }
            }

            var line = new OpeningStockLine
            {
                ItemCode = selected.ItemCode,
                ItemName = selected.ItemName,
                Mrp = mrp,
                Qty = qty,
                PurchaseRate = purchaseRate,
                SalesRate = salesRate
            };

            _lines.Add(line);

            ClearEntryLine();
            Dispatcher.BeginInvoke(new Action(() => cmbEntryItemName.Focus()), DispatcherPriority.Input);
        }

        // ════════════════════════════════════════════════════════════════════
        // SFDATAGRID — GRID EVENTS
        // ════════════════════════════════════════════════════════════════════
        private void SfLines_CurrentCellEndEdit(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellEndEditEventArgs e)
        {
            if (sender is not Syncfusion.UI.Xaml.Grid.SfDataGrid grid) return;

            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is not OpeningStockLine row)
                return;

            int colIndex = grid.ResolveToGridVisibleColumnIndex(e.RowColumnIndex.ColumnIndex);
            string col = (colIndex >= 0 && colIndex < grid.Columns.Count)
                ? grid.Columns[colIndex].MappingName
                : "";

            if (col == "ItemCode" && !string.IsNullOrWhiteSpace(row.ItemCode))
            {
                var found = _repo.GetItemByCode(row.ItemCode.Trim());
                if (found != null)
                    PopulateLineRow(row, found);
                else
                    MessageBox.Show($"Item code \"{row.ItemCode}\" was not found.",
                        "Item Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SfLines_RowValidating(object sender, Syncfusion.UI.Xaml.Grid.RowValidatingEventArgs e)
        {
            if (e.RowData is OpeningStockLine row)
            {
                if (string.IsNullOrWhiteSpace(row.ItemCode))
                {
                    e.IsValid = false;
                    e.ErrorMessages["ItemCode"] = "Item code is required.";
                }
                else if (row.Qty <= 0)
                {
                    e.IsValid = false;
                    e.ErrorMessages["Qty"] = "Quantity must be greater than zero.";
                }
            }
        }

        private void GridItemCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemLookup selected)
                return;

            if (sender is SmartComboBox cmb && cmb.DataContext is OpeningStockLine row)
            {
                PopulateLineRow(row, selected);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    int rowIndex = sfLines.ResolveToRowIndex(row);
                    var qtyColumn = sfLines.Columns.FirstOrDefault(c => c.MappingName == "Qty");

                    if (qtyColumn != null && rowIndex >= 0)
                    {
                        int colIndex = sfLines.Columns.IndexOf(qtyColumn);
                        int visibleColIndex = sfLines.ResolveToGridVisibleColumnIndex(colIndex);

                        sfLines.MoveCurrentCell(
                            new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIndex, visibleColIndex));
                        sfLines.SelectionController.CurrentCellManager?.BeginEdit();
                    }
                }), DispatcherPriority.Input);
            }
        }

        private static void PopulateLineRow(OpeningStockLine row, ItemLookup src)
        {
            row.ItemCode = src.ItemCode;
            row.ItemName = src.ItemName;
            row.Mrp = src.Mrp;
            row.PurchaseRate = src.UnitPricePurchase;
            row.SalesRate = src.UnitPriceSales;
        }

        private void BtnDeleteLineRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is OpeningStockLine row)
                _lines.Remove(row);
        }
    }
}