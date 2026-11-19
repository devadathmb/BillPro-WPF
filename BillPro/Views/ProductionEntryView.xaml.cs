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
    public partial class ProductionEntryView : UserControl
    {
        private readonly ProductionRepository _repo;
        private readonly ProductFormulaRepository _formulaRepo;

        // Component lookup (Raw Materials + Consumables) shared by the entry line
        // and the in-grid SmartComboBox — same idea as AllItemsList in Purchase Entry.
        public ObservableCollection<ItemMaster> AllComponentItems { get; } = new();

        private ObservableCollection<ProductionItemDetail> _itemRows = new();

        private string _saveMode = "Save";   // "Save" | "Update"
        private int _editingProductionNo = 0;
        private bool _isLoadingForEdit = false;

        public ProductionEntryView()
        {
            try
            {
                InitializeComponent();

                _repo = new ProductionRepository();
                _repo.InitializeDatabase();

                _formulaRepo = new ProductFormulaRepository();
                _formulaRepo.InitializeDatabase();

                sfItems.ItemsSource = _itemRows;

                SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;

                DisableFields();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"CRITICAL STARTUP ERROR:\n\n{ex.Message}\n\n{ex.InnerException?.Message}",
                    "Fatal Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            LoadLookups();
        }

        // ── Data loading ─────────────────────────────────────────────

        private void LoadLookups()
        {
            // Top picker: only items that can BE produced — Finished Goods + Consumables
            var currentFg = cmbFinishedGoods.SelectedValue;
            cmbFinishedGoods.ItemsSource = _formulaRepo.GetFormulaTargetItems();
            if (currentFg != null) cmbFinishedGoods.SelectedValue = currentFg;

            // Component lookup: Raw Materials + Consumables
            AllComponentItems.Clear();
            foreach (var item in _formulaRepo.GetAllRawMaterials())
                AllComponentItems.Add(item);
        }

        // ── Enable / disable / clear ────────────────────────────────

        private void EnableFields()
        {
            dpProductionDate.IsEnabled = true;
            cmbFinishedGoods.IsEnabled = true;
            txtProducedQty.IsEnabled = true;
            cmbMrp.IsEnabled = true;
            txtLabourCharge.IsEnabled = true;
            txtOtherExpense.IsEnabled = true;
        }

        private void DisableFields()
        {
            dpProductionDate.IsEnabled = false;
            cmbFinishedGoods.IsEnabled = false;
            txtProducedQty.IsEnabled = false;
            cmbMrp.IsEnabled = false;
            txtLabourCharge.IsEnabled = false;
            txtOtherExpense.IsEnabled = false;
            EnableEntryLine(false);
            sfItems.IsEnabled = false;
        }

        private void EnableEntryLine(bool enabled)
        {
            txtEntryCode.IsEnabled = enabled;
            cmbEntryItemName.IsEnabled = enabled;
            txtEntryQty.IsEnabled = enabled;
            txtEntryWastage.IsEnabled = enabled;
            txtEntryRate.IsEnabled = enabled;
            btnAddItem.IsEnabled = enabled;
        }

        private void ClearAll()
        {
            txtProductionNo.Text = "";
            dpProductionDate.SelectedDate = DateTime.Today;
            cmbFinishedGoods.SelectedIndex = -1;
            txtProducedQty.Text = "";
            cmbMrp.ItemsSource = null;
            cmbMrp.Text = "";
            txtLabourCharge.Text = "0";
            txtOtherExpense.Text = "0";
            _itemRows.Clear();
            ClearEntryLine();
            RecalcTotals();
        }

        private void ClearEntryLine()
        {
            txtEntryCode.Text = "";
            cmbEntryItemName.SelectedIndex = -1;
            cmbEntryItemName.Text = "";
            txtEntryQty.Text = "";
            txtEntryWastage.Text = "0";
            txtEntryRate.Text = "";
            txtEntryAmount.Text = "0.00";
        }

        // ── New / Display / Save / Delete / Close ───────────────────

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            LoadLookups();
            ClearAll();
            EnableFields();
            sfItems.IsEnabled = true;

            _saveMode = "Save";
            _editingProductionNo = 0;

            txtProductionNo.Text = _repo.GetNextProductionNo().ToString();

            btnSave.Content = "Save";
            btnSave.IsEnabled = false;   // stays disabled until an item is picked
            btnDelete.IsEnabled = false;
            cmbFinishedGoods.Focus();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var all = _repo.GetAllSummaries();

            var config = new SearchConfig
            {
                Title = "Search Production Entries",
                SearchProperties = new List<string> { "ProductionNo", "ItemName" },
                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "No",        BindingPath = "ProductionNo", Width = "70" },
                    new SearchColumn { Header = "Date",       BindingPath = "ProductionDate", Width = "100" },
                    new SearchColumn { Header = "Item",       BindingPath = "ItemName", Width = "*" },
                    new SearchColumn { Header = "Qty",        BindingPath = "ProducedQty", Width = "80" },
                    new SearchColumn { Header = "Grand Total",BindingPath = "GrandTotal", Width = "100" }
                }
            };

            SearchOverlay.LoadData(all.Cast<object>(), config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is ProductionSummary selected)
                LoadForEdit(selected.ProductionNo);
        }

        private void LoadForEdit(int productionNo)
        {
            var summary = _repo.GetSummary(productionNo);
            if (summary == null) return;

            _isLoadingForEdit = true;
            try
            {
                LoadLookups();
                EnableFields();
                sfItems.IsEnabled = true;

                _saveMode = "Update";
                _editingProductionNo = productionNo;

                txtProductionNo.Text = summary.ProductionNo.ToString();
                dpProductionDate.SelectedDate = DateTime.TryParse(summary.ProductionDate, out var d) ? d : DateTime.Today;
                cmbFinishedGoods.SelectedValue = summary.ItemCode;
                txtProducedQty.Text = summary.ProducedQty.ToString("0.###");

                // Populate the batch list for this item, then override the text with
                // exactly what was saved (LoadMrpOptions' own default is for New only).
                LoadMrpOptions(summary.ItemCode);
                cmbMrp.Text = summary.Mrp.ToString("0.00");

                txtLabourCharge.Text = summary.LabourCharge.ToString("0.00");
                txtOtherExpense.Text = summary.OtherExpense.ToString("0.00");

                _itemRows.Clear();
                // Loaded rows are treated as manual (IsFormulaLine = false) so that
                // editing Produced Qty during a review doesn't silently overwrite the
                // saved consumption figures — the grid stays fully editable either way.
                foreach (var row in _repo.GetItems(productionNo))
                {
                    row.IsFormulaLine = false;
                    _itemRows.Add(row);
                }

                EnableEntryLine(true);
                RecalcTotals();

                btnSave.Content = "Update";
                btnSave.IsEnabled = true;
                btnDelete.IsEnabled = true;
            }
            finally
            {
                _isLoadingForEdit = false;
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (_editingProductionNo <= 0) return;

            if (MessageBox.Show("This production entry will be permanently deleted!\n\nAre you sure?",
                "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    _repo.DeleteProduction(_editingProductionNo);
                    ClearAll();
                    DisableFields();
                    btnSave.IsEnabled = false;
                    btnDelete.IsEnabled = false;
                    MessageBox.Show("Production entry deleted successfully.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error deleting record:\n" + ex.Message, "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            sfItems.SelectionController.CurrentCellManager?.EndEdit();

            if (cmbFinishedGoods.SelectedItem is not ItemMaster fg)
            {
                MessageBox.Show("Please select the produced item name!", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbFinishedGoods.Focus();
                return;
            }

            if (!double.TryParse(txtProducedQty.Text, out double producedQty) || producedQty <= 0)
            {
                MessageBox.Show("Please enter a valid Produced Qty!", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtProducedQty.Focus();
                return;
            }

            if (_itemRows.Count == 0)
            {
                MessageBox.Show("Please add at least one item in the consumption grid.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!double.TryParse(cmbMrp.Text, out double mrp) || mrp <= 0)
            {
                MessageBox.Show("Please enter a valid MRP for the produced item (pick an existing batch or type a new MRP).",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                cmbMrp.Focus();
                return;
            }

            double.TryParse(txtLabourCharge.Text, out double labour);
            double.TryParse(txtOtherExpense.Text, out double other);

            double itemTotal = _itemRows.Sum(r => r.Amount);
            double grandTotal = itemTotal + labour + other;
            double averageCost = producedQty == 0 ? 0 : Math.Round(grandTotal / producedQty, 2);

            var summary = new ProductionSummary
            {
                ProductionNo = int.Parse(txtProductionNo.Text),
                ProductionDate = (dpProductionDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd"),
                ItemCode = fg.ItemCode,
                ProducedQty = producedQty,
                ItemTotal = itemTotal,
                LabourCharge = labour,
                OtherExpense = other,
                GrandTotal = grandTotal,
                AverageCost = averageCost,
                Mrp = mrp
            };

            try
            {
                if (_saveMode == "Save")
                {
                    int newNo = _repo.SaveNew(summary, _itemRows.ToList(), "USER");
                    _editingProductionNo = newNo;
                    MessageBox.Show("Production entry saved successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    summary.ProductionNo = _editingProductionNo;
                    _repo.UpdateExisting(summary, _itemRows.ToList(), "USER");
                    MessageBox.Show("Production entry updated successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                DisableFields();
                btnSave.IsEnabled = false;
                btnDelete.IsEnabled = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving record:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Item picker (top) ────────────────────────────────────────

        private void CmbFinishedGoods_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingForEdit) return;
            if (cmbFinishedGoods.SelectedItem is not ItemMaster fg) return;

            LoadFormulaLines(fg.ItemCode);
            LoadMrpOptions(fg.ItemCode);
            EnableEntryLine(true);
            btnSave.IsEnabled = true;
            Dispatcher.BeginInvoke(new Action(() => txtProducedQty.Focus()), DispatcherPriority.Input);
        }

        /// <summary>
        /// Populates the MRP / Batch combo box with the produced item's existing
        /// batches (so an existing one can be picked to top up), and pre-fills the
        /// editable text with a sensible default — the last/first existing batch's
        /// MRP if one exists, otherwise the item master's default MRP. Either way
        /// the field stays fully editable: typing any other value defines a new batch.
        /// </summary>
        private void LoadMrpOptions(string itemCode)
        {
            var batches = _repo.GetBatchesForItem(itemCode)
                .Select(b => new ProductionBatchOption { Mrp = b.Mrp, CurrentQty = b.CurrentQty })
                .OrderBy(b => b.Mrp)
                .ToList();

            cmbMrp.ItemsSource = batches;

            if (batches.Count > 0)
            {
                cmbMrp.Text = batches[0].Mrp.ToString("0.00");
            }
            else
            {
                double defaultMrp = _repo.GetItemDefaultMrp(itemCode);
                cmbMrp.Text = defaultMrp.ToString("0.00");
            }
        }

        /// <summary>
        /// Rebuilds the consumption grid from the selected item's formula.
        /// Manually added rows (IsFormulaLine = false) are preserved.
        /// </summary>
        private void LoadFormulaLines(string finishedGoodCode)
        {
            double.TryParse(txtProducedQty.Text, out double producedQty);

            var manualRows = _itemRows.Where(r => !r.IsFormulaLine).ToList();
            _itemRows.Clear();

            int sno = 1;
            foreach (var f in _formulaRepo.GetFormulaDetails(finishedGoodCode))
            {
                var comp = _formulaRepo.GetItem(f.RawMaterialCode);
                double rate = comp?.UnitPricePurchase ?? 0;

                _itemRows.Add(new ProductionItemDetail
                {
                    SNo = sno++,
                    Code = f.RawMaterialCode,
                    ItemName = f.ItemName,
                    PerUnitQuantity = f.Quantity,
                    Quantity = f.Quantity * producedQty,
                    Rate = rate,
                    IsFormulaLine = true
                });
            }

            foreach (var m in manualRows)
            {
                m.SNo = sno++;
                _itemRows.Add(m);
            }

            RecalcTotals();
        }

        /// <summary>Recomputes formula-derived rows' Quantity whenever Produced Qty changes.</summary>
        private void TxtProducedQty_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingForEdit) return;
            if (!double.TryParse(txtProducedQty.Text, out double producedQty)) producedQty = 0;

            foreach (var row in _itemRows.Where(r => r.IsFormulaLine))
                row.Quantity = row.PerUnitQuantity * producedQty;

            RecalcTotals();
        }

        // ── Entry line — add a component manually ────────────────────

        private void TxtEntryCode_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                string code = txtEntryCode.Text.Trim();
                if (!string.IsNullOrWhiteSpace(code)) LookupAndFillByCode(code);
            }
        }

        private void TxtEntryCode_LostFocus(object sender, RoutedEventArgs e)
        {
            string code = txtEntryCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(code)) return;
            if (cmbEntryItemName.SelectedItem is ItemMaster already
                && string.Equals(already.ItemCode, code, StringComparison.OrdinalIgnoreCase))
                return;

            LookupAndFillByCode(code);
        }

        private void LookupAndFillByCode(string code)
        {
            var found = AllComponentItems.FirstOrDefault(i =>
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
                MessageBox.Show($"Item code \"{code}\" was not found, or is not a Raw Material/Consumable.",
                    "Item Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryCode.Clear();
                Dispatcher.BeginInvoke(new Action(() => txtEntryCode.Focus()), DispatcherPriority.Input);
            }
        }

        private void CmbEntryItemName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemMaster selected) return;
            FillEntryLine(selected);
            Dispatcher.BeginInvoke(new Action(() => txtEntryQty.Focus()), DispatcherPriority.Input);
        }

        private void FillEntryLine(ItemMaster item)
        {
            txtEntryCode.Text = item.ItemCode;
            txtEntryRate.Text = item.UnitPricePurchase.ToString("0.00");
            txtEntryWastage.Text = "0";
            txtEntryQty.Text = "";
            txtEntryAmount.Text = "0.00";
        }

        /// <summary>Live preview of Amount = (Qty + Wastage) × Rate while filling the entry line.</summary>
        private void EntryCalculation_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Guard: Text="0" on txtEntryWastage in XAML fires this during
            // InitializeComponent(), before later-declared controls (like
            // txtEntryAmount) exist yet.
            if (txtEntryQty == null || txtEntryWastage == null || txtEntryRate == null || txtEntryAmount == null)
                return;

            double.TryParse(txtEntryQty.Text, out double qty);
            double.TryParse(txtEntryWastage.Text, out double wastage);
            double.TryParse(txtEntryRate.Text, out double rate);
            txtEntryAmount.Text = Math.Round((qty + wastage) * rate, 2).ToString("N2");
        }

        private void BtnAddItem_Click(object sender, RoutedEventArgs e) => CommitEntryLine();

        private void CommitEntryLine()
        {
            if (cmbEntryItemName.SelectedItem is not ItemMaster selected)
            {
                MessageBox.Show("Please select a valid item from the list.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                cmbEntryItemName.Focus();
                return;
            }

            if (!double.TryParse(txtEntryQty.Text, out double qty) || qty <= 0)
            {
                MessageBox.Show("Please enter a valid quantity greater than zero.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryQty.Focus();
                return;
            }

            double.TryParse(txtEntryWastage.Text, out double wastage);
            double.TryParse(txtEntryRate.Text, out double rate);

            // If this component is already on the grid, update it instead of duplicating.
            var existing = _itemRows.FirstOrDefault(r =>
                string.Equals(r.Code, selected.ItemCode, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Quantity = qty;
                existing.Wastage = wastage;
                existing.Rate = rate;
            }
            else
            {
                _itemRows.Add(new ProductionItemDetail
                {
                    SNo = _itemRows.Count + 1,
                    Code = selected.ItemCode,
                    ItemName = selected.ItemName,
                    Quantity = qty,
                    Wastage = wastage,
                    Rate = rate,
                    IsFormulaLine = false
                });
            }

            RecalcTotals();
            ClearEntryLine();
            Dispatcher.BeginInvoke(new Action(() => cmbEntryItemName.Focus()), DispatcherPriority.Input);
        }

        // ── Consumption grid events ──────────────────────────────────

        private void SfItems_CurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
        {
            if (sender is not SfDataGrid grid) return;
            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is not ProductionItemDetail row) return;

            int colIndex = grid.ResolveToGridVisibleColumnIndex(e.RowColumnIndex.ColumnIndex);
            string col = (colIndex >= 0 && colIndex < grid.Columns.Count) ? grid.Columns[colIndex].MappingName : "";

            // A manual edit to a formula-derived row's Qty/Rate means the user is
            // overriding it deliberately — stop auto-recalculating this specific
            // row from Produced Qty from now on.
            if (col == "Quantity" || col == "Rate" || col == "Wastage")
                row.IsFormulaLine = false;

            RecalcTotals();
        }

        private void SfItems_RowValidating(object sender, RowValidatingEventArgs e)
        {
            if (e.RowData is ProductionItemDetail row && string.IsNullOrWhiteSpace(row.Code))
            {
                e.IsValid = false;
                e.ErrorMessages["Code"] = "Item code is required.";
            }
        }

        private void GridItemCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemMaster selected) return;
            if (sender is not SmartComboBox cmb || cmb.DataContext is not ProductionItemDetail row) return;

            row.Code = selected.ItemCode;
            row.ItemName = selected.ItemName;
            row.Rate = selected.UnitPricePurchase;
            row.IsFormulaLine = false;

            RecalcTotals();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                int rowIndex = sfItems.ResolveToRowIndex(row);
                var qtyColumn = sfItems.Columns.FirstOrDefault(c => c.MappingName == "Quantity");
                if (qtyColumn != null && rowIndex >= 0)
                {
                    int colIndex = sfItems.Columns.IndexOf(qtyColumn);
                    int visibleColIndex = sfItems.ResolveToGridVisibleColumnIndex(colIndex);
                    sfItems.MoveCurrentCell(new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIndex, visibleColIndex));
                    sfItems.SelectionController.CurrentCellManager?.BeginEdit();
                }
            }), DispatcherPriority.Input);
        }

        private void BtnDeleteItemRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProductionItemDetail row)
            {
                _itemRows.Remove(row);
                RecalcTotals();
            }
        }

        // ── Totals ────────────────────────────────────────────────────

        private void FooterAmount_TextChanged(object sender, TextChangedEventArgs e) => RecalcTotals();

        private void RecalcTotals()
        {
            // Guard: Text="0" on txtLabourCharge/txtOtherExpense in XAML fires
            // FooterAmount_TextChanged during InitializeComponent(), before the
            // totals labels (declared later in the XAML) exist yet.
            if (lblItemTotal == null || lblAverageCost == null || lblGrandTotal == null)
                return;

            double itemTotal = _itemRows.Sum(r => r.Amount);
            double.TryParse(txtLabourCharge.Text, out double labour);
            double.TryParse(txtOtherExpense.Text, out double other);
            double.TryParse(txtProducedQty.Text, out double producedQty);

            double grandTotal = itemTotal + labour + other;
            double averageCost = producedQty <= 0 ? 0 : Math.Round(grandTotal / producedQty, 2);

            lblItemTotal.Text = itemTotal.ToString("N2");
            lblAverageCost.Text = averageCost.ToString("N2");
            lblGrandTotal.Text = grandTotal.ToString("N2");
        }
    }
}