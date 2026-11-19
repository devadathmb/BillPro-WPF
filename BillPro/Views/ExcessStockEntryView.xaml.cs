using Syncfusion.UI.Xaml.Grid.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    /// <summary>
    /// Excess Stock Entry — dedicated screen for ExcessStockRepository.
    /// MRP stays editable in the entry line and the grid: an Excess entry is
    /// allowed to land on a batch that doesn't exist yet (see
    /// ExcessStockRepository.InsertLines / GetOrCreateBatch).
    ///
    /// Lifecycle mirrors SalesEntryView: fields are fully disabled after a
    /// successful Save/Update and fully re-enabled on New. "Delete" has been
    /// replaced with "Cancel" — cancelling keeps the entry row (for audit),
    /// reverses its stock effect via the repository, and displays a
    /// CANCELLED watermark, same idea as SalesEntryView.CancelBill.
    /// </summary>
    public partial class ExcessStockEntryView : UserControl, IDisposable
    {
        private readonly ExcessStockRepository _repository = new ExcessStockRepository();

        private string _saveMode = "Save";
        private int _currentEntryNo;
        private bool _isCancelledEntryLoaded = false;

        private readonly ObservableCollection<StockAdjustmentLine> _lines = new();

        // Quantity captured just before an in-grid cell edit begins, so
        // SfItems_CurrentCellEndEdit can roll back to it if the operator
        // tries to commit a zero/negative quantity directly in the grid.
        private double _qtyBeforeGridEdit = 0;

        /// <summary>Every item/batch in the system — source for the item entry
        /// combo (same GetBatchPickerLookups list the Individual Stock Report
        /// picker uses), so picking one auto-fills MRP/Purchase Rate below.</summary>
        public List<ItemBatchPickerLookup> AllItemBatches { get; private set; } = new();

        public ExcessStockEntryView()
        {
            InitializeComponent();

            sfItems.ItemsSource = _lines;

            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += (s, e) => btnDisplay.Focus();

            EntryEditNavigator.Subscribe<int>(EntryChannels.ExcessStockEntry, OnEditRequested);

            DisableFields();
        }

        public void Dispose()
        {
            EntryEditNavigator.Unsubscribe<int>(EntryChannels.ExcessStockEntry, OnEditRequested);
        }

        private void OnEditRequested(int entryNo) => LoadEntryForEdit(entryNo);

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _repository.InitializeDatabase();
            LoadAllItemBatches();
        }

        private void LoadAllItemBatches()
        {
            var stockRepo = new StockMasterRepository();
            AllItemBatches = stockRepo.GetBatchPickerLookups();

            // A single constant-key grouping is just a trick to get the
            // GroupStyle.HeaderTemplate (Code/Item Name/Batch/MRP/Purch.
            // Rate/Stock column header) to render once above the list —
            // there's only ever one group, so nothing is actually being
            // split apart.
            var view = CollectionViewSource.GetDefaultView(AllItemBatches);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new PropertyGroupDescription(null, new ConstantGroupConverter()));

            cmbEntryItemName.ItemsSource = view;
        }

        // ── FIELD ENABLE/DISABLE / CLEAR ─────────────────────────────────

        private void EnableFields()
        {
            dpEntryDate.IsEnabled = true;
            txtRemarks.IsEnabled = true;
            cmbEntryItemName.IsEnabled = true;
            txtEntryMrp.IsEnabled = true;
            txtEntryPurchaseRate.IsEnabled = true;
            txtEntryQty.IsEnabled = true;
            btnAddItem.IsEnabled = true;
            sfItems.IsEnabled = true;
        }

        private void DisableFields()
        {
            dpEntryDate.IsEnabled = false;
            txtRemarks.IsEnabled = false;
            cmbEntryItemName.IsEnabled = false;
            txtEntryMrp.IsEnabled = false;
            txtEntryPurchaseRate.IsEnabled = false;
            txtEntryQty.IsEnabled = false;
            btnAddItem.IsEnabled = false;
            sfItems.IsEnabled = false;
        }

        private void ClearFields()
        {
            txtRemarks.Text = "";
            dpEntryDate.SelectedDate = DateTime.Now;
            ClearEntryLine();
            _lines.Clear();
            RecalcTotals();
        }

        private void ClearEntryLine()
        {
            cmbEntryItemName.SelectedIndex = -1;
            cmbEntryItemName.Text = "";
            txtEntryMrp.Text = "0";
            txtEntryPurchaseRate.Text = "0";
            txtEntryQty.Text = "0";
        }

        // ── NEW / DISPLAY / SAVE / CANCEL ────────────────────────────────

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            EnableFields();
            ClearFields();
            _saveMode = "Save";
            _currentEntryNo = 0;
            _isCancelledEntryLoaded = false;
            pnlCancelledWatermark.Visibility = Visibility.Collapsed;

            RefreshEntryNoPreview();

            btnSave.Content = "Save";
            btnSave.IsEnabled = true;
            btnCancelEntry.IsEnabled = false;
            cmbEntryItemName.Focus();
        }

        private void RefreshEntryNoPreview()
        {
            txtEntryNo.Text = ExcessStockRepository.FormatEntryNo(_repository.GetNextEntryNo());
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allEntries = _repository.GetAll();

            var config = new SearchConfig
            {
                Title = "Search Excess Stock Entries",
                SearchProperties = new List<string> { "EntryNo", "Remarks" },
                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "Entry No", BindingPath = "FormattedEntryNo", Width = "90" },
                    new SearchColumn { Header = "Date", BindingPath = "EntryDate", Width = "100" },
                    new SearchColumn { Header = "Remarks", BindingPath = "Remarks", Width = "*" },
                    new SearchColumn { Header = "Total Qty", BindingPath = "TotalQty", Width = "100" },
                    new SearchColumn { Header = "Cancelled", BindingPath = "IsCancelled", Width = "80" },
                },
                TargetOnSelect = txtEntryNo
            };

            SearchOverlay.LoadData(allEntries, config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is StockAdjustmentSummary selected)
                LoadEntryForEdit(selected.EntryNo);
        }

        public void LoadEntryForEdit(int entryNo)
        {
            try
            {
                var summary = _repository.GetSummary(entryNo);
                if (summary == null) return;

                // NOTE: requires an IsCancelled (int, 0/1) property on
                // StockAdjustmentSummary, populated by GetSummary.
                _isCancelledEntryLoaded = summary.IsCancelled == 1;

                EnableFields();
                _saveMode = "Update";
                _currentEntryNo = entryNo;

                txtEntryNo.Text = summary.FormattedEntryNo;
                txtRemarks.Text = summary.Remarks;

                _lines.Clear();
                foreach (var line in _repository.GetLines(entryNo))
                    _lines.Add(line);

                ClearEntryLine();
                RecalcTotals();

                btnSave.Content = "Update";

                if (_isCancelledEntryLoaded)
                {
                    pnlCancelledWatermark.Visibility = Visibility.Visible;
                    DisableFields();
                    btnSave.IsEnabled = false;
                    btnCancelEntry.IsEnabled = false;
                }
                else
                {
                    pnlCancelledWatermark.Visibility = Visibility.Collapsed;
                    btnSave.IsEnabled = true;
                    btnCancelEntry.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading entry:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (dpEntryDate.SelectedDate == null)
            {
                MessageBox.Show("Please pick an entry date.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_lines.Count == 0)
            {
                MessageBox.Show("Add at least one item before saving.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_lines.Any(l => l.Qty <= 0))
            {
                MessageBox.Show("All lines must have a quantity greater than 0.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var summary = new StockAdjustmentSummary
                {
                    EntryNo = _currentEntryNo,
                    EntryDate = dpEntryDate.SelectedDate.Value.ToString("yyyy-MM-dd"),
                    Remarks = txtRemarks.Text.Trim()
                };

                var lines = _lines.ToList();

                if (_saveMode == "Save")
                {
                    _currentEntryNo = _repository.SaveNew(summary, lines, "USER");
                    MessageBox.Show("Entry saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    _repository.UpdateExisting(summary, lines, "USER");
                    MessageBox.Show("Entry updated successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                txtEntryNo.Text = ExcessStockRepository.FormatEntryNo(summary.EntryNo);

                // Fully lock the form down after a successful save/update —
                // previously only the buttons were toggled, leaving every
                // field still editable with nothing to save the edits into.
                DisableFields();
                btnSave.IsEnabled = false;
                btnCancelEntry.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving entry:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancelEntry_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (_currentEntryNo == 0) return;

            if (MessageBox.Show(
                    "This will cancel this entry and reverse its stock effect.\n" +
                    "The entry is kept for audit — this cannot easily be undone.\n\nContinue?",
                    "Confirm Cancel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                // ⚠ ADAPT: ExcessStockRepository needs a CancelEntry method that
                // (1) reverses the stock quantity this entry's lines applied,
                // (2) sets IsCancelled = 1 / CancelledBy / CancelledDate on the
                // summary row, and (3) does NOT delete the summary or its lines
                // (kept for audit) — mirrors SalesRepository.CancelBill.
                _repository.CancelEntry(_currentEntryNo, currentUser: "USER");

                MessageBox.Show("Entry cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);

                ClearFields();
                DisableFields();
                _currentEntryNo = 0;
                _isCancelledEntryLoaded = false;
                pnlCancelledWatermark.Visibility = Visibility.Collapsed;
                txtEntryNo.Text = "";
                btnSave.IsEnabled = false;
                btnCancelEntry.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error cancelling entry:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── ITEM ENTRY LINE ──────────────────────────────────────────────

        private void CmbEntryItemName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbEntryItemName.SelectedItem is ItemBatchPickerLookup picked)
            {
                txtEntryMrp.Text = picked.Mrp.ToString("0.00");
                txtEntryPurchaseRate.Text = picked.PurchaseRate.ToString("0.00");
            }
        }

        private void BtnAddItem_Click(object sender, RoutedEventArgs e)
        {
            if (cmbEntryItemName.SelectedItem is not ItemBatchPickerLookup picked)
            {
                MessageBox.Show("Please pick an item.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                cmbEntryItemName.Focus();
                return;
            }

            double qty = ToDouble(txtEntryQty.Text);
            if (qty <= 0)
            {
                MessageBox.Show("Quantity must be greater than 0.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                txtEntryQty.Focus();
                return;
            }

            var line = new StockAdjustmentLine
            {
                ItemCode = picked.ItemCode,
                ItemName = picked.ItemName,
                Mrp = ToDouble(txtEntryMrp.Text),
                PurchaseRate = ToDouble(txtEntryPurchaseRate.Text),
                Qty = qty
            };

            _lines.Add(line);
            RenumberRows();
            RecalcTotals();

            ClearEntryLine();
            cmbEntryItemName.Focus();
        }

        private void BtnDeleteItemRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is StockAdjustmentLine line)
            {
                _lines.Remove(line);
                RenumberRows();
                RecalcTotals();
            }
        }

        // Captures the quantity as it stood immediately before a grid cell
        // edit begins, so an invalid commit (<= 0) can be rolled back to a
        // known-good value instead of just being recalculated as-is.
        private void SfItems_CurrentCellBeginEdit(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellBeginEditEventArgs e)
        {
            if (sfItems.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is StockAdjustmentLine line)
                _qtyBeforeGridEdit = line.Qty;
        }

        private void SfItems_CurrentCellEndEdit(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellEndEditEventArgs e)
        {
            if (sfItems.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is StockAdjustmentLine line)
            {
                if (line.Qty <= 0)
                {
                    MessageBox.Show("Quantity must be greater than 0.", "Invalid Quantity",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    line.Qty = _qtyBeforeGridEdit;
                    sfItems.View.Refresh();
                }
            }

            RecalcTotals();
        }

        private void RenumberRows()
        {
            int sno = 1;
            foreach (var line in _lines) line.SNo = sno++;
        }

        private void RecalcTotals()
        {
            lblTotalPurchaseRate.Text = _lines.Sum(l => l.PurchaseRate).ToString("0.00");
            lblTotalQty.Text = _lines.Sum(l => l.Qty).ToString("0.000");
        }

        private double ToDouble(string text)
        {
            double.TryParse(text, out double val);
            return val;
        }
    }
}