using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BillPro.Controls;
using BillPro.Models;
using BillPro.Repositories;
// Alias to resolve ambiguity with Syncfusion.UI.Xaml.Grid.SelectionChangedEventArgs
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace BillPro.Views
{
    public class FormulaLineItem : BillPro.Helpers.ObservableObject
    {
        private int _sNo;
        public int SNo
        {
            get => _sNo;
            set => SetProperty(ref _sNo, value);
        }

        private string _code = "";
        public string Code
        {
            get => _code;
            set => SetProperty(ref _code, value);
        }

        private string _itemName = "";
        public string ItemName
        {
            get => _itemName;
            set => SetProperty(ref _itemName, value);
        }

        private double _quantity;
        public double Quantity
        {
            get => _quantity;
            set => SetProperty(ref _quantity, value);
        }
    }

    public partial class ProductFormulaEntryView : UserControl
    {
        private readonly ProductFormulaRepository _repo;
        private readonly ObservableCollection<FormulaLineItem> _lines = new();

        // Component lookup (Raw Materials + Consumables) bound to the entry-line
        // combo AND the in-grid SmartComboBox (Item Name column's EditTemplate).
        public ObservableCollection<ItemMaster> AllRawMaterials { get; } = new();

        private const int COL_QTY = 3;

        public ProductFormulaEntryView()
        {
            InitializeComponent();
            try
            {
                _repo = new ProductFormulaRepository();
                _repo.InitializeDatabase();

                dgFormula.ItemsSource = _lines;
                btnSave.IsEnabled = false;

                Loaded += (s, e) =>
                {
                    LoadFinishedGoods();
                    LoadRawMaterials();
                    cmbFinishedGoods.Focus();
                    cmbFinishedGoods.IsDropDownOpen = true;
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Startup error: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Data loading ────────────────────────────────────────────

        /// <summary>
        /// Item picker for "what does this formula belong to" — only
        /// Finished Goods (0) and Consumables (2) can have a formula.
        /// </summary>
        private void LoadFinishedGoods()
        {
            cmbFinishedGoods.ItemsSource = _repo.GetFormulaTargetItems();
        }

        /// <summary>Populates the component lookup — Raw Materials (1) + Consumables (2).</summary>
        private void LoadRawMaterials()
        {
            AllRawMaterials.Clear();
            foreach (var item in _repo.GetAllRawMaterials())
                AllRawMaterials.Add(item);
        }

        private void LoadFormulaDetails(string fgCode)
        {
            _lines.Clear();
            int r = 1;
            foreach (var d in _repo.GetFormulaDetails(fgCode))
                _lines.Add(new FormulaLineItem
                {
                    SNo = r++,
                    Code = d.RawMaterialCode,
                    ItemName = d.ItemName,
                    Quantity = d.Quantity
                });

            EnableEntryLine(true);
        }

        private void EnableEntryLine(bool enabled)
        {
            cmbEntryItemName.IsEnabled = enabled;
            txtEntryQty.IsEnabled = enabled;
            btnAddLine.IsEnabled = enabled;
        }

        // ── Finished-goods / consumable combo (top picker) ─────────────

        private void CmbFinishedGoods_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                CommitFinishedGoodSelection();
            }
        }

        private void CommitFinishedGoodSelection()
        {
            if (cmbFinishedGoods.SelectedItem is ItemMaster fg)
            {
                LoadFormulaDetails(fg.ItemCode);
                btnSave.IsEnabled = true;

                Dispatcher.BeginInvoke(new Action(() => cmbEntryItemName.Focus()),
                    DispatcherPriority.Input);
            }
        }

        // ── Entry line — pick a component, enter Qty, Add ───────────────

        private void CmbEntryItemName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemMaster selected) return;

            // Leave Qty blank so the user always types it fresh
            txtEntryQty.Text = "";
            Dispatcher.BeginInvoke(new Action(() => txtEntryQty.Focus()), DispatcherPriority.Input);
        }

        private void BtnAddLine_Click(object sender, RoutedEventArgs e) => CommitEntryLine();

        private void CommitEntryLine()
        {
            if (cmbEntryItemName.SelectedItem is not ItemMaster selected)
            {
                MessageBox.Show("Please select a valid component from the list.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                cmbEntryItemName.Focus();
                return;
            }

            if (!double.TryParse(txtEntryQty.Text, out double qty) || qty <= 0)
            {
                MessageBox.Show("Please enter a valid quantity greater than zero.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryQty.Focus();
                return;
            }

            // If this component is already on the grid, just update its quantity
            // instead of adding a duplicate row.
            var existing = _lines.FirstOrDefault(l =>
                string.Equals(l.Code, selected.ItemCode, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Quantity = qty;
            }
            else
            {
                _lines.Add(new FormulaLineItem
                {
                    SNo = _lines.Count + 1,
                    Code = selected.ItemCode,
                    ItemName = selected.ItemName,
                    Quantity = qty
                });
            }

            cmbEntryItemName.SelectedIndex = -1;
            cmbEntryItemName.Text = "";
            txtEntryQty.Text = "";
            Dispatcher.BeginInvoke(new Action(() => cmbEntryItemName.Focus()), DispatcherPriority.Input);
        }

        // ── Grid edit handlers (corrections to already-added lines) ─────

        private void DgFormula_RowValidating(object sender, RowValidatingEventArgs e)
        {
            if (e.RowData is FormulaLineItem line && string.IsNullOrWhiteSpace(line.Code))
            {
                e.IsValid = false;
                e.ErrorMessages["Code"] = "Item code is required.";
            }
        }

        private void DgFormula_CurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
        {
            // Quantity edits need no extra handling — two-way binding covers it.
            // Item Name corrections are handled by GridItemName_SelectionChanged below.
        }

        /// <summary>
        /// SmartComboBox SelectionChanged inside the grid's Item Name edit
        /// template — lets the user correct which component a line refers to.
        /// </summary>
        private void GridItemName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemMaster selected)
                return;

            if (sender is not SmartComboBox cmb || cmb.DataContext is not FormulaLineItem row)
                return;

            row.Code = selected.ItemCode;
            row.ItemName = selected.ItemName;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                int rowIndex = dgFormula.ResolveToRowIndex(row);
                var qtyColumn = dgFormula.Columns.FirstOrDefault(c => c.MappingName == "Quantity");

                if (qtyColumn != null && rowIndex >= 0)
                {
                    int colIndex = dgFormula.Columns.IndexOf(qtyColumn);
                    int visibleColIndex = dgFormula.ResolveToGridVisibleColumnIndex(colIndex);

                    dgFormula.MoveCurrentCell(
                        new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIndex, visibleColIndex));
                    dgFormula.SelectionController.CurrentCellManager?.BeginEdit();
                }
            }), DispatcherPriority.Input);
        }

        // ── Delete row ───────────────────────────────────────────────

        private void BtnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FormulaLineItem selectedLine)
            {
                _lines.Remove(selectedLine);
                RenumberLines();
            }
        }

        // ── Save / Next / Close ──────────────────────────────────────

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            dgFormula.SelectionController.CurrentCellManager?.EndEdit();

            if (cmbFinishedGoods.SelectedItem == null)
            {
                MessageBox.Show("Please specify the Item Name!", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbFinishedGoods.Focus();
                return;
            }

            if (_lines.Count == 0)
            {
                MessageBox.Show("Please add at least one component to the formula.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbEntryItemName.Focus();
                return;
            }

            foreach (var row in _lines)
            {
                if (row.Quantity <= 0)
                {
                    MessageBox.Show($"Row {row.SNo} has an invalid quantity.",
                        "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            try
            {
                string fgCode = ((ItemMaster)cmbFinishedGoods.SelectedItem).ItemCode;
                var details = _lines.Select(l => new ProductFormulaDetail
                {
                    FinishedGoodCode = fgCode,
                    RawMaterialCode = l.Code,
                    ItemName = l.ItemName,
                    Quantity = l.Quantity
                }).ToList();

                _repo.SaveFormulaDetails(fgCode, details);
                MessageBox.Show("Formula saved successfully!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                // Lock UI fields after save
                btnSave.IsEnabled = false;
                cmbFinishedGoods.IsEnabled = false;
                dgFormula.IsEnabled = false;
                EnableEntryLine(false);

                // Move focus to Next button
                Dispatcher.BeginInvoke(new Action(() => btnNext.Focus()), DispatcherPriority.Input);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving formula:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            // Clear data
            _lines.Clear();
            dgFormula.SelectedItem = null;
            cmbFinishedGoods.SelectedIndex = -1;
            cmbFinishedGoods.Text = "";

            // Unlock UI fields for next entry
            cmbFinishedGoods.IsEnabled = true;
            dgFormula.IsEnabled = true;

            EnableEntryLine(false); // Kept false until a Finished Good is actually selected
            btnSave.IsEnabled = false;

            // Return focus to the starting combo box
            Dispatcher.BeginInvoke(new Action(() => cmbFinishedGoods.Focus()), DispatcherPriority.Input);
        }

        // ── Navigation helpers ───────────────────────────────────────

        private void RenumberLines()
        {
            int n = 1;
            foreach (var l in _lines)
                l.SNo = n++;
        }
    }
}