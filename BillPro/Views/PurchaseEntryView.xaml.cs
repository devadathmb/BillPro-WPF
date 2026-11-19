using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Syncfusion.UI.Xaml.Grid;
// Alias to resolve ambiguity with Syncfusion.UI.Xaml.Grid.SelectionChangedEventArgs
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using BillPro.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;
using Syncfusion.UI.Xaml.Grid.Helpers;

namespace BillPro.Views
{
    public partial class PurchaseEntryView : UserControl, IDisposable
    {
        // ── Repository ───────────────────────────────────────────────────────
        private readonly PurchaseRepository _repo;

        // ── Lookup data (loaded once at startup / refreshed on tab visibility) ─
        public ObservableCollection<ItemLookup> AllItemsList { get; } = new();
        public ObservableCollection<AcHeadLookup> AllAcHeadsList { get; } = new();
        public ObservableCollection<AcHeadLookup> AllBankHeadsList { get; } = new();
        private List<VendorCustomer> _allVendors = new();

        // ── Grid data ────────────────────────────────────────────────────────
        private ObservableCollection<PurchaseItemDetail> _itemRows = new();
        private ObservableCollection<PurchaseExternalExpense> _expenseRows = new();

        // ── State ────────────────────────────────────────────────────────────
        private string _saveMode = "Save";   // "Save" | "Update"
        private int _editingPurchaseNo = 0;
        private string _originalCreatedBy = "";
        private DateTime _originalCreatedDate = DateTime.Now;
        private bool _isCalculatingEntry = false;
        private string _lastSelectedBankHeadCode = null;

        // Quantity captured just before an in-grid cell edit begins, so
        // SfItems_CurrentCellEndEdit can roll the cell back if the committed
        // value is invalid (<= 0). Mirrors SalesEntryView's _qtyBeforeGridEdit.
        private decimal _qtyBeforeGridEdit = 0;

        // Settlement (To Bank / To Cash / To Credit) cascade state — same
        // pattern as SalesEntryView's By Cash / By Bank / By Credit:
        // _toBankManuallyEdited stops the default-to-net-total behavior once
        // the user edits either settlement field themselves; _isRecalculatingSettlement
        // guards against re-entrancy since the three fields recalculate from each other.
        private bool _toBankManuallyEdited = false;
        private bool _isRecalculatingSettlement = false;

        // Set for the duration of LoadBillForEdit(). While true, RecalcTotals()
        // still refreshes the Amount/Tax/Grand/Net Total labels (so they're
        // correct as items stream in), but leaves To Bank / To Cash / To Credit
        // completely alone — those are populated verbatim from the saved bill.
        // Without this, every _itemRows.Add() during the load fires
        // RecalcTotals() -> RecalcSettlement(), which recomputes To Credit
        // from whatever partial totals exist at that instant and stomps the
        // saved credit split (usually straight to 0) before the real values
        // are even assigned.
        private bool _isLoadingBillForEdit = false;

        // ════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════
        public PurchaseEntryView()
        {
            InitializeComponent();

            this.IsVisibleChanged += PurchaseEntryView_IsVisibleChanged;
            _repo = new PurchaseRepository();
            _repo.InitializeDatabase();

            // Wire up the search overlay
            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;

            // Bind grid sources
            sfItems.ItemsSource = _itemRows;
            sfExpenses.ItemsSource = _expenseRows;
            cmbBankHead.ItemsSource = AllBankHeadsList;
            // cmbCashVendorLookup shares the same vendor list; bound after
            // LoadLookups() fills _allVendors, so assigned in LoadLookups.

            // Subscribe to collection changes so totals recalculate live
            _itemRows.CollectionChanged += (_, __) => RecalcTotals();
            _expenseRows.CollectionChanged += (_, __) => RecalcTotals();

            LoadLookups();

            // Lets PurchaseReportView (or any other report) ask this view to
            // load a specific bill for editing, e.g. via a right-click
            // "Edit This Bill" option, without either view needing a direct
            // reference to the other. Subscribed once for the lifetime of
            // this instance — see Dispose() for why we don't use Unloaded.
            EntryEditNavigator.Subscribe<int>(EntryChannels.PurchaseEntry, OnEditRequested);
        }

        /// <summary>
        /// Called by MainWindow when this view's tab is actually closed
        /// (LayoutDocument.Closed), NOT when the tab is merely switched away
        /// from. Unsubscribing here — instead of on UserControl.Unloaded —
        /// is what keeps "Edit This Bill" from a report working even when
        /// the Purchase Entry tab is open but not the active tab: AvalonDock
        /// detaches an inactive tab's content from the visual tree (firing
        /// Unloaded) purely as part of switching tabs, well before the tab
        /// itself is closed, and re-attaches it (firing Loaded) when you
        /// switch back. Unsubscribing on Unloaded killed the bridge the
        /// moment you looked at any other tab.
        /// </summary>
        public void Dispose()
        {
            EntryEditNavigator.Unsubscribe<int>(EntryChannels.PurchaseEntry, OnEditRequested);
        }

        private void PurchaseEntryView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
            {
                // Execute after the UI has finished its current render pass
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshMasterData();
                }), DispatcherPriority.Loaded);
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                btnNew.Focus()));
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

                AllAcHeadsList.Clear();
                foreach (var head in _repo.GetAcHeadLookups())
                    AllAcHeadsList.Add(head);

                AllBankHeadsList.Clear();
                foreach (var head in _repo.GetBankAcHeads())
                    AllBankHeadsList.Add(head);

                _allVendors = _repo.GetVendors();
                cmbVendorName.ItemsSource = _allVendors;
                cmbCashVendorLookup.ItemsSource = _allVendors;

                if (AllBankHeadsList.Count > 0)
                {
                    if (_lastSelectedBankHeadCode != null &&
                        AllBankHeadsList.Any(h => h.AcHeadCode.ToString() == _lastSelectedBankHeadCode))
                        cmbBankHead.SelectedValue = _lastSelectedBankHeadCode;
                    else
                        cmbBankHead.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading lookup data:\n" + ex.Message,
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Re-pulls lookups (items, vendors, account heads, bank heads) from
        /// the database while preserving whatever the user currently has
        /// selected. Fired on tab-visibility rather than only at startup, so
        /// edits made in Item Master / Vendor Master / Account Head Master in
        /// another tab show up here without restarting the app.
        /// </summary>
        private void RefreshMasterData()
        {
            try
            {
                // 1. CACHE CURRENT SELECTIONS — so we don't clear the user's work
                var currentVendor = cmbVendorName.SelectedValue;
                var currentEntryItem = cmbEntryItemName.SelectedValue;
                var currentBankHead = cmbBankHead.SelectedValue;

                // 2. FETCH LATEST DATA FROM DATABASE
                var latestItems = _repo.GetItemLookups();
                var latestVendors = _repo.GetVendors();
                var latestAcHeads = _repo.GetAcHeadLookups();
                var latestBankHeads = _repo.GetBankAcHeads();

                // 3. UPDATE THE BINDINGS
                _allVendors = latestVendors.ToList();
                cmbVendorName.ItemsSource = _allVendors;
                cmbCashVendorLookup.ItemsSource = _allVendors;

                AllItemsList.Clear();
                foreach (var item in latestItems)
                    AllItemsList.Add(item);

                AllAcHeadsList.Clear();
                foreach (var head in latestAcHeads)
                    AllAcHeadsList.Add(head);

                AllBankHeadsList.Clear();
                foreach (var head in latestBankHeads)
                    AllBankHeadsList.Add(head);

                // 4. RESTORE SELECTIONS — exactly as the user left them
                if (currentVendor != null)
                    cmbVendorName.SelectedValue = currentVendor;

                if (currentEntryItem != null)
                    cmbEntryItemName.SelectedValue = currentEntryItem;

                if (currentBankHead != null)
                    cmbBankHead.SelectedValue = currentBankHead;
            }
            catch (Exception ex)
            {
                // Fail gracefully if the database is busy while switching tabs
                MessageBox.Show("Error refreshing data:\n" + ex.Message,
                    "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // UI STATE HELPERS
        // ════════════════════════════════════════════════════════════════════
        private void ToggleFields(bool enabled)
        {
            dpPurchaseDate.IsEnabled = enabled;
            txtBillNo.IsEnabled = enabled;
            dpBillDate.IsEnabled = enabled;
            cmbPurchaseMode.IsEnabled = enabled;

            if (enabled) ApplyVendorMode();
            else
            {
                txtCashVendorName.IsEnabled = false;
                cmbVendorName.IsEnabled = false;
            }

            txtAddress1.IsEnabled = enabled;
            txtAddress2.IsEnabled = enabled;
            txtGstNo.IsEnabled = enabled;
            txtPhone.IsEnabled = enabled;
            txtMobile.IsEnabled = enabled;
            txtState.IsEnabled = enabled;

            sfItems.IsEnabled = enabled;
            sfExpenses.IsEnabled = enabled;

            txtCashDiscount.IsEnabled = enabled;
            txtRoundedAmount.IsEnabled = enabled;

            txtToBank.IsEnabled = enabled;
            cmbBankHead.IsEnabled = enabled;
            txtToCash.IsEnabled = enabled;
            txtToCredit.IsEnabled = enabled;

            // ── Entry line controls ───────────────────────────────────────
            ToggleItemEntryLine(enabled);
            ToggleExpenseEntryLine(enabled);

            btnSave.IsEnabled = enabled;
        }

        /// <summary>Enables or disables every control in the item entry line.</summary>
        private void ToggleItemEntryLine(bool enabled)
        {
            txtEntryItemCode.IsEnabled = enabled;
            cmbEntryItemName.IsEnabled = enabled;
            txtEntryMrp.IsEnabled = enabled;
            txtEntryRate.IsEnabled = enabled;
            txtEntryDiscount.IsEnabled = enabled;
            txtEntryQty.IsEnabled = enabled;
            txtEntryTaxRate.IsEnabled = enabled;
            btnAddItem.IsEnabled = enabled;
            txtEntryDiscountAmount.IsEnabled = enabled;
            txtEntryTaxAmount.IsEnabled = enabled;
            txtEntryAmount.IsEnabled = enabled;
        }

        /// <summary>Enables or disables every control in the expense entry line.</summary>
        private void ToggleExpenseEntryLine(bool enabled)
        {
            cmbEntryAcHead.IsEnabled = enabled;
            txtEntryNarration.IsEnabled = enabled;
            txtEntryCredit.IsEnabled = enabled;
            txtEntryDebit.IsEnabled = enabled;
            txtEntryExpTaxRate.IsEnabled = enabled;
            btnAddExpense.IsEnabled = enabled;
        }

        private void ClearAll()
        {
            dpPurchaseDate.SelectedDate = DateTime.Today;
            dpBillDate.SelectedDate = DateTime.Today;
            txtBillNo.Text = "";
            cmbPurchaseMode.SelectedIndex = 0;

            ClearVendorDetails();

            _itemRows.Clear();
            _expenseRows.Clear();

            ClearItemEntryLine();
            ClearExpenseEntryLine();

            txtCashDiscount.Text = "0";
            txtRoundedAmount.Text = "0";

            txtToBank.Text = "0";
            if (cmbBankHead.SelectedValue != null)
                _lastSelectedBankHeadCode = cmbBankHead.SelectedValue.ToString();
            txtToCash.Text = "0";
            txtToCredit.Text = "0";
            _toBankManuallyEdited = false;

            RecalcTotals();
        }

        private void ClearVendorDetails()
        {
            txtCashVendorName.Text = "";
            cmbVendorName.SelectedIndex = -1;
            cmbVendorName.Text = "";
            txtAddress1.Text = "";
            txtAddress2.Text = "";
            txtGstNo.Text = "";
            txtPhone.Text = "";
            txtMobile.Text = "";
            txtState.Text = "";
        }

        /// <summary>Resets every control in the item entry line to its blank state.</summary>
        private void ClearItemEntryLine()
        {
            txtEntryItemCode.Text = "";

            // Detach handler to avoid false-positive SelectionChanged
            cmbEntryItemName.SelectionChanged -= CmbEntryItemName_SelectionChanged;
            cmbEntryItemName.SelectedIndex = -1;
            cmbEntryItemName.Text = "";
            cmbEntryItemName.SelectionChanged += CmbEntryItemName_SelectionChanged;

            txtEntryHsn.Text = "";
            txtEntryMrp.Text = "";
            txtEntryRate.Text = "";
            txtEntryDiscount.Text = "0.00";
            txtEntryQty.Text = "";
            txtEntryTaxRate.Text = "";
        }

        /// <summary>Resets every control in the expense entry line to its blank state.</summary>
        private void ClearExpenseEntryLine()
        {
            cmbEntryAcHead.SelectionChanged -= CmbEntryAcHead_SelectionChanged;
            cmbEntryAcHead.SelectedIndex = -1;
            cmbEntryAcHead.Text = "";
            cmbEntryAcHead.SelectionChanged += CmbEntryAcHead_SelectionChanged;

            txtEntryNarration.Text = "";
            txtEntryCredit.Text = "0.00";
            txtEntryDebit.Text = "0.00";
            txtEntryExpTaxRate.Text = "0.00";
        }

        private void ApplyVendorMode()
        {
            bool isCredit = cmbPurchaseMode.SelectedIndex == 1;
            gridCashVendor.Visibility = isCredit ? Visibility.Collapsed : Visibility.Visible;
            cmbVendorName.Visibility = isCredit ? Visibility.Visible : Visibility.Collapsed;
            txtCashVendorName.IsEnabled = !isCredit;
            cmbVendorName.IsEnabled = isCredit;
        }

        // ════════════════════════════════════════════════════════════════════
        // CASH MODE VENDOR LOOKUP POPUP  (F1 to open)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>F1 opens the vendor lookup popup over the cash vendor text box.</summary>
        private void TxtCashVendorName_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                cmbCashVendorLookup.SelectedIndex = -1;
                cmbCashVendorLookup.Text = "";
                popupVendorLookup.IsOpen = true;
                e.Handled = true;
            }
        }

        /// <summary>Once the popup is fully open, focus and open the lookup combo immediately
        /// so the user can start typing a name right away without an extra click.</summary>
        private void PopupVendorLookup_Opened(object sender, EventArgs e)
        {
            cmbCashVendorLookup.Focus();
            cmbCashVendorLookup.IsDropDownOpen = true;
        }

        /// <summary>Esc closes the popup and returns focus to the text box without changing anything.</summary>
        private void CmbCashVendorLookup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                popupVendorLookup.IsOpen = false;
                txtCashVendorName.Focus();
                e.Handled = true;
            }
        }

        /// <summary>When a vendor is selected from the lookup popup, fill all address/contact
        /// fields exactly as CmbVendorName_SelectionChanged does for credit mode.</summary>
        private void CmbCashVendorLookup_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbCashVendorLookup.SelectedItem is not VendorCustomer vendor) return;

            popupVendorLookup.IsOpen = false;

            txtCashVendorName.Text = vendor.CustomerName;
            FillVendorDetails(vendor);

            // Return focus to the next meaningful field (bill date / item entry)
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => txtCashVendorName.Focus()));
        }

        /// <summary>Shared helper — populates address/GST/phone from a VendorCustomer.
        /// Called from both cash-lookup and credit-combo selection changed.</summary>
        private void FillVendorDetails(VendorCustomer vendor)
        {
            txtAddress1.Text = vendor.Address1 ?? "";
            txtAddress2.Text = vendor.Address2 ?? "";
            txtGstNo.Text = vendor.GstNo ?? "";
            txtPhone.Text = vendor.Phone ?? "";
            txtMobile.Text = vendor.Mobile ?? "";
            //txtState.Text = vendor.State ?? "";
        }

        // ════════════════════════════════════════════════════════════════════
        // HEADER COMBO EVENTS — Purchase Mode & Vendor
        // ════════════════════════════════════════════════════════════════════
        private void CmbPurchaseMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyVendorMode();
            ClearVendorDetails();
            RecalcSettlement(recalcFromBank: true);
        }

        private void CmbVendorName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbVendorName.SelectedItem is VendorCustomer v)
                FillVendorDetails(v);
        }

        private void CmbBankHead_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selection is read directly from cmbBankHead in BuildSummaryFromUI;
            // nothing else needs to react when the bank account changes.
        }

        // ════════════════════════════════════════════════════════════════════
        // ITEM ENTRY LINE
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Enter / Tab on the Item Code box → look up and auto-populate.</summary>
        private void TxtEntryItemCode_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                LookupAndFillItemByCode(txtEntryItemCode.Text.Trim());
                e.Handled = true;   // prevent default Enter action
            }
            // Tab is handled by LostFocus below — no action needed here
        }

        /// <summary>
        /// When focus leaves the Code box (including on Tab), try a lookup if
        /// text was entered without using the combo.
        /// </summary>
        private void TxtEntryItemCode_LostFocus(object sender, RoutedEventArgs e)
        {
            string code = txtEntryItemCode.Text.Trim();

            // Only act if there's a code AND the combo doesn't already show this item
            if (string.IsNullOrWhiteSpace(code)) return;
            if (cmbEntryItemName.SelectedItem is ItemLookup already
                && string.Equals(already.ItemCode, code, StringComparison.OrdinalIgnoreCase))
                return;

            LookupAndFillItemByCode(code);
        }

        /// <summary>
        /// Core lookup: finds the item in the already-loaded list (no extra DB call),
        /// populates the entry line, and moves focus to Qty.
        /// Shows an error and resets if the code is invalid.
        /// </summary>
        private void LookupAndFillItemByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            var found = AllItemsList.FirstOrDefault(i =>
                string.Equals(i.ItemCode, code, StringComparison.OrdinalIgnoreCase));

            if (found != null)
            {
                // Suppress the SelectionChanged handler re-firing the same logic
                cmbEntryItemName.SelectionChanged -= CmbEntryItemName_SelectionChanged;
                cmbEntryItemName.SelectedItem = found;
                cmbEntryItemName.SelectionChanged += CmbEntryItemName_SelectionChanged;

                FillItemEntryLine(found);
                Dispatcher.BeginInvoke(new Action(() => txtEntryMrp.Focus()),
                    DispatcherPriority.Input);
            }
            else
            {
                MessageBox.Show($"Item code \"{code}\" was not found.",
                    "Item Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryItemCode.Clear();
                Dispatcher.BeginInvoke(new Action(() => txtEntryItemCode.Focus()),
                    DispatcherPriority.Input);
            }
        }

        /// <summary>
        /// SmartComboBox selection in the entry line — auto-populate fields and
        /// move focus straight to Qty.
        /// </summary>
        private void CmbEntryItemName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemLookup selected) return;

            FillItemEntryLine(selected);
            Dispatcher.BeginInvoke(new Action(() => txtEntryMrp.Focus()),
                DispatcherPriority.Input);
        }

        /// <summary>Populates all read-only and editable fields from the chosen ItemLookup.</summary>
        private void FillItemEntryLine(ItemLookup item)
        {
            txtEntryItemCode.Text = item.ItemCode;
            txtEntryHsn.Text = item.HsnCode;
            txtEntryMrp.Text = ((decimal)item.Mrp).ToString("N2");
            txtEntryRate.Text = ((decimal)item.UnitPricePurchase).ToString("N2");
            txtEntryDiscount.Text = "0.00";
            txtEntryTaxRate.Text = item.TaxRate.ToString("N2");
            // Leave Qty blank so the user always types it fresh
            txtEntryQty.Text = "";
        }

        /// <summary>Enter in Qty field — currently a no-op; Add Item is via button only.</summary>
        private void TxtEntryQty_KeyDown(object sender, KeyEventArgs e)
        {
            //if (e.Key == Key.Return)
            //{
            //    CommitItemEntry();
            //    e.Handled = true;
            //}
        }

        private void BtnAddItem_Click(object sender, RoutedEventArgs e) => CommitItemEntry();

        /// <summary>
        /// Validates the entry line, builds a PurchaseItemDetail, adds it to the
        /// grid's collection, and resets the entry line for the next item.
        /// </summary>
        private void CommitItemEntry()
        {
            // Must have a valid item selected
            if (cmbEntryItemName.SelectedItem is not ItemLookup selected)
            {
                MessageBox.Show("Please select a valid item from the list.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                cmbEntryItemName.Focus();
                return;
            }

            // Qty is mandatory
            if (!decimal.TryParse(txtEntryQty.Text, out decimal qty) || qty <= 0)
            {
                MessageBox.Show("Please enter a valid quantity greater than zero.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryQty.Focus();
                return;
            }

            // Safely parse all explicit values from the text boxes to preserve manual edits
            decimal.TryParse(txtEntryRate.Text, out decimal rate);
            decimal.TryParse(txtEntryDiscount.Text, out decimal discRate);
            decimal.TryParse(txtEntryDiscountAmount.Text, out decimal discAmt);
            decimal.TryParse(txtEntryTaxRate.Text, out decimal taxRate);
            decimal.TryParse(txtEntryTaxAmount.Text, out decimal taxAmt);
            decimal.TryParse(txtEntryAmount.Text, out decimal price);
            decimal.TryParse(txtEntryTotal.Text, out decimal total);

            var row = new PurchaseItemDetail
            {
                ItemCode = selected.ItemCode,
                ItemName = selected.ItemName,
                HsnCode = selected.HsnCode,
                Mrp = (decimal)selected.Mrp,
                Rate = rate,
                Quantity = qty,

                // Map the new editable fields!
                DiscountRate = discRate,
                Discount = discAmt,
                TaxRate = taxRate,
                TaxAmount = taxAmt,
                Price = price,
                Total = total
            };

            // Note: row.RecalcAll() is INTENTIONALLY REMOVED here.
            // We want to trust the values pulled directly from the UI so any manual overrides are preserved.

            _itemRows.Add(row);
            RecalcTotals();

            // Reset the entry line and put focus back on the Item Name combo
            ClearItemEntryLine();
            Dispatcher.BeginInvoke(new Action(() => cmbEntryItemName.Focus()),
                DispatcherPriority.Input);
        }

        // ════════════════════════════════════════════════════════════════════
        // SFDATAGRID — ITEMS GRID EVENTS
        // ════════════════════════════════════════════════════════════════════

        private void SfItems_CurrentCellBeginEdit(object sender, CurrentCellBeginEditEventArgs e)
        {
            if (sender is not SfDataGrid grid) return;
            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is PurchaseItemDetail row)
                _qtyBeforeGridEdit = row.Quantity;
        }

        private void SfItems_CurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
        {
            if (sender is not SfDataGrid grid) return;

            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is not PurchaseItemDetail row)
                return;

            // Reject a zero/negative quantity committed directly in the grid —
            // roll the cell back to whatever it held before this edit began
            // instead of letting an invalid value flow into totals. Mirrors
            // SalesEntryView's SfItems_CurrentCellEndEdit.
            //
            // IMPORTANT: this must happen SYNCHRONOUSLY, inside this same
            // handler call — not deferred via Dispatcher.BeginInvoke. This
            // handler fires while Syncfusion is still mid-way through
            // committing the edit into the grid's own internal record/editor
            // state; if the rollback is posted to run on a later dispatcher
            // cycle, the grid finishes its own commit pass (using the
            // invalid value) before that callback ever runs, and the
            // deferred assignment either loses the race or gets stomped by
            // the grid re-syncing to its already-committed (invalid) state.
            // Setting row.Quantity back right here, before returning, is
            // what makes the rollback actually stick.
            if (row.Quantity <= 0)
            {
                MessageBox.Show("Quantity must be greater than 0.", "Invalid Quantity",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                row.Quantity = _qtyBeforeGridEdit;
                grid.View.Refresh();
                RecalcTotals();
                return;
            }

            int colIndex = grid.ResolveToGridVisibleColumnIndex(e.RowColumnIndex.ColumnIndex);
            string col = (colIndex >= 0 && colIndex < grid.Columns.Count)
                ? grid.Columns[colIndex].MappingName
                : "";

            // Allow typing an item code directly into the grid Code cell
            if (col == "ItemCode" && !string.IsNullOrWhiteSpace(row.ItemCode))
            {
                var found = _repo.GetItemByCode(row.ItemCode.Trim());
                if (found != null)
                    PopulateItemRow(row, found);
                else
                    MessageBox.Show($"Item code \"{row.ItemCode}\" was not found.",
                        "Item Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RecalcTotals();
        }

        // NOTE: A RowValidating handler previously lived here (SfItems_RowValidating),
        // enforcing "ItemCode required" / "Quantity > 0" at the grid level.
        // It has been REMOVED — Syncfusion's SfDataGrid.RowValidating pipeline
        // walks every DataColumnBase (including unbound/template columns such
        // as the Delete-button column, which has a null MappingName) and does
        // e.ErrorMessages.ContainsKey(column.MappingName) for each one. A null
        // MappingName throws ArgumentNullException deep inside Syncfusion's
        // ValidationHelper, crashing the app on row commit (e.g. pressing
        // Enter/Tab out of a grid row) regardless of whether this handler's
        // own logic ever ran.
        //
        // No validation coverage is lost by removing it: both "ItemCode
        // required" and "Quantity > 0" are already enforced earlier, in
        // CommitItemEntry() (blocks adding a row unless a valid ItemLookup is
        // selected and qty > 0), and again as a final safety net in
        // BtnSave_Click via the items.Where(...) filter before Save/Update.
        // Sales' equivalent grid (sfItems in SalesEntryView) never had a
        // RowValidating handler for the same reason — validate at commit-time
        // and save-time, not inside the grid's own validation pipeline.

        /// <summary>
        /// SmartComboBox SelectionChanged inside the items grid edit template.
        /// </summary>
        private void GridItemCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ItemLookup selected)
                return;

            if (sender is SmartComboBox cmb && cmb.DataContext is PurchaseItemDetail row)
            {
                PopulateItemRow(row, selected);
                RecalcTotals();

                // Move focus to Quantity column
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    int rowIndex = sfItems.ResolveToRowIndex(row);
                    var qtyColumn = sfItems.Columns.FirstOrDefault(c => c.MappingName == "Quantity");

                    if (qtyColumn != null && rowIndex >= 0)
                    {
                        int colIndex = sfItems.Columns.IndexOf(qtyColumn);
                        int visibleColIndex = sfItems.ResolveToGridVisibleColumnIndex(colIndex);

                        sfItems.MoveCurrentCell(
                            new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIndex, visibleColIndex));
                        sfItems.SelectionController.CurrentCellManager?.BeginEdit();
                    }
                }), DispatcherPriority.Input);
            }
        }

        private static void PopulateItemRow(PurchaseItemDetail row, ItemLookup src)
        {
            row.ItemCode = src.ItemCode;
            row.ItemName = src.ItemName;
            row.HsnCode = src.HsnCode;
            row.Mrp = (decimal)src.Mrp;
            row.Rate = (decimal)src.UnitPricePurchase;
            row.TaxRate = (decimal)src.TaxRate;
        }

        /// <summary>Delete button clicked on an items grid row.</summary>
        private void BtnDeleteItemRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PurchaseItemDetail row)
            {
                _itemRows.Remove(row);
                RecalcTotals();
            }
        }

        /// <summary>
        /// Rate / Qty / Discount / Tax cascade for the item entry line.
        /// </summary>
        private void EntryCalculation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isCalculatingEntry) return;
            if (sender is not TextBox tb) return;

            // Safely parse all current values
            decimal rate = decimal.TryParse(txtEntryRate.Text, out var r) ? r : 0;
            decimal qty = decimal.TryParse(txtEntryQty.Text, out var q) ? q : 0;
            decimal discPercent = decimal.TryParse(txtEntryDiscount.Text, out var dp) ? dp : 0;
            decimal discAmt = decimal.TryParse(txtEntryDiscountAmount.Text, out var da) ? da : 0;
            decimal taxRate = decimal.TryParse(txtEntryTaxRate.Text, out var tr) ? tr : 0;
            decimal taxAmt = decimal.TryParse(txtEntryTaxAmount.Text, out var ta) ? ta : 0;
            decimal amount = decimal.TryParse(txtEntryAmount.Text, out var a) ? a : 0;

            _isCalculatingEntry = true;
            try
            {
                // 1. Rate, Qty, or Discount % → recalculate everything
                if (tb == txtEntryRate || tb == txtEntryQty || tb == txtEntryDiscount)
                {
                    decimal gross = Math.Round(rate * qty, 2);
                    discAmt = Math.Round(gross * (discPercent / 100m), 2);
                    txtEntryDiscountAmount.Text = discAmt.ToString("0.00");

                    amount = gross - discAmt;
                    txtEntryAmount.Text = amount.ToString("0.00");

                    taxAmt = Math.Round(amount * (taxRate / 100m), 2);
                    txtEntryTaxAmount.Text = taxAmt.ToString("0.00");
                }
                // 2. Discount Amount → recalculate Amount and Tax
                else if (tb == txtEntryDiscountAmount)
                {
                    decimal gross = Math.Round(rate * qty, 2);
                    amount = gross - discAmt;
                    txtEntryAmount.Text = amount.ToString("0.00");

                    taxAmt = Math.Round(amount * (taxRate / 100m), 2);
                    txtEntryTaxAmount.Text = taxAmt.ToString("0.00");
                }
                // 3. Amount → recalculate Tax only
                else if (tb == txtEntryAmount)
                {
                    taxAmt = Math.Round(amount * (taxRate / 100m), 2);
                    txtEntryTaxAmount.Text = taxAmt.ToString("0.00");
                }
                // 4. Tax Rate → recalculate Tax Amount from current Amount
                else if (tb == txtEntryTaxRate)
                {
                    taxAmt = Math.Round(amount * (taxRate / 100m), 2);
                    txtEntryTaxAmount.Text = taxAmt.ToString("0.00");
                }
                // 5. Tax Amount manually edited → just let Total update below
                // (tb == txtEntryTaxAmount: user overriding tax directly, no upstream change)

                // ALWAYS recalculate Total
                decimal total = amount + taxAmt;
                txtEntryTotal.Text = total.ToString("0.00");
            }
            finally
            {
                _isCalculatingEntry = false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // EXPENSE ENTRY LINE
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// SmartComboBox selection in the expense entry line — sets the code
        /// and moves focus to Narration.
        /// </summary>
        private void CmbEntryAcHead_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not AcHeadLookup) return;

            // Move focus to narration so the user can type a description
            Dispatcher.BeginInvoke(new Action(() => txtEntryNarration.Focus()),
                DispatcherPriority.Input);
        }

        private void BtnAddExpense_Click(object sender, RoutedEventArgs e) => CommitExpenseEntry();

        /// <summary>
        /// Validates the expense entry line, builds a PurchaseExternalExpense,
        /// adds it to the grid's collection, and resets the entry line.
        /// </summary>
        private void CommitExpenseEntry()
        {
            if (cmbEntryAcHead.SelectedItem is not AcHeadLookup selected)
            {
                MessageBox.Show("Please select a valid account head.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                cmbEntryAcHead.Focus();
                return;
            }

            decimal.TryParse(txtEntryCredit.Text, out decimal credit);
            decimal.TryParse(txtEntryDebit.Text, out decimal debit);
            decimal.TryParse(txtEntryExpTaxRate.Text, out decimal taxRate);

            if (credit == 0 && debit == 0)
            {
                MessageBox.Show("Please enter a Credit or Debit amount.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryDebit.Focus();
                return;
            }

            var row = new PurchaseExternalExpense
            {
                AcHeadCode = selected.AcHeadCode,
                AcHeadName = selected.AcHeadName,
                Narration = txtEntryNarration.Text.Trim(),
                Credit = credit,
                Debit = debit,
                ExternalTaxRate = taxRate,
            };
            _expenseRows.Add(row);
            RecalcTotals();

            ClearExpenseEntryLine();
            Dispatcher.BeginInvoke(new Action(() => cmbEntryAcHead.Focus()),
                DispatcherPriority.Input);
        }

        // ════════════════════════════════════════════════════════════════════
        // SFDATAGRID — EXPENSES GRID EVENTS
        // ════════════════════════════════════════════════════════════════════
        private void SfExpenses_CurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
        {
            if (sender is not SfDataGrid grid) return;

            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is PurchaseExternalExpense)
                RecalcTotals();
        }

        /// <summary>Delete button clicked on an expenses grid row.</summary>
        private void BtnDeleteExpenseRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PurchaseExternalExpense row)
            {
                _expenseRows.Remove(row);
                RecalcTotals();
            }
        }

        /// <summary>SmartComboBox SelectionChanged inside the Expenses grid.</summary>
        private void GridAcHeadCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not AcHeadLookup selected)
                return;

            if (sender is SmartComboBox cmb && cmb.DataContext is PurchaseExternalExpense row)
            {
                row.AcHeadCode = selected.AcHeadCode;
                row.AcHeadName = selected.AcHeadName;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    int rowIndex = sfExpenses.ResolveToRowIndex(row);
                    var debitColumn = sfExpenses.Columns.FirstOrDefault(c => c.MappingName == "Debit");

                    if (debitColumn != null && rowIndex >= 0)
                    {
                        int colIndex = sfExpenses.Columns.IndexOf(debitColumn);
                        int visibleColIndex = sfExpenses.ResolveToGridVisibleColumnIndex(colIndex);

                        sfExpenses.MoveCurrentCell(
                            new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIndex, visibleColIndex));
                        sfExpenses.SelectionController.CurrentCellManager?.BeginEdit();
                    }
                }), DispatcherPriority.Input);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // TOTALS
        // ════════════════════════════════════════════════════════════════════
        private void FooterAmount_TextChanged(object sender, TextChangedEventArgs e) => RecalcTotals();

        private void RecalcTotals()
        {
            decimal amountTotal = _itemRows.Sum(r => r.Price);
            decimal taxTotal = _itemRows.Sum(r => r.TaxAmount);
            decimal grandTotal = amountTotal + taxTotal;

            decimal.TryParse(txtCashDiscount.Text, out decimal cashDiscount);
            decimal.TryParse(txtRoundedAmount.Text, out decimal rounded);

            decimal netTotal = grandTotal + rounded - cashDiscount;

            lblAmountTotal.Text = amountTotal.ToString("N2");
            lblTaxTotal.Text = taxTotal.ToString("N2");
            lblGrandTotal.Text = grandTotal.ToString("N2");
            lblNetTotal.Text = netTotal.ToString("N2");

            // While loading a saved bill, To Bank / To Cash / To Credit are
            // being set verbatim from the saved values in LoadBillForEdit —
            // leave them alone here so the CollectionChanged-triggered calls
            // fired while item rows are still streaming in don't stomp them
            // with partial running totals (see _isLoadingBillForEdit).
            if (_isLoadingBillForEdit) return;

            // Default "To Bank" to the full net total — the common case is the
            // whole bill going to the bank. Once the user edits either
            // settlement field themselves, stop overwriting it here; their
            // split takes over.
            if (!_toBankManuallyEdited)
                txtToBank.Text = netTotal.ToString("0.00");

            RecalcSettlement(netTotal);
        }

        // ════════════════════════════════════════════════════════════════════
        // SETTLEMENT CASCADE (To Bank / To Cash / To Credit)
        //   Editing To Cash : To Bank = Net Total − To Cash
        //   Editing To Bank : To Cash remains untouched
        //   To Credit (always) = Net Total − To Bank − To Cash
        // ════════════════════════════════════════════════════════════════════
        private void SettlementAmount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isRecalculatingSettlement || _isLoadingBillForEdit) return;

            if (sender == txtToBank)
            {
                if (txtToBank.IsKeyboardFocused)
                    _toBankManuallyEdited = true;

                RecalcSettlement(recalcFromBank: true);
            }
            else if (sender == txtToCash)
            {
                // User is editing To Cash directly — derive To Bank from it,
                // AND mark the split as manually pinned. Without this, adding
                // another item later would silently reset To Bank back to the
                // full net total and blow away the split the user just set up.
                // (Mirrors SalesEntryView's txtByCash handling.)
                if (txtToCash.IsKeyboardFocused)
                    _toBankManuallyEdited = true;

                RecalcSettlement(recalcFromBank: false);
            }
        }

        private void RecalcSettlement(decimal? netTotalOverride = null, bool recalcFromBank = true)
        {
            if (_isRecalculatingSettlement || _isLoadingBillForEdit) return;
            _isRecalculatingSettlement = true;
            try
            {
                decimal netTotal = netTotalOverride
                    ?? (decimal.TryParse(lblNetTotal.Text, out decimal nt) ? nt : 0);

                // When To Cash is updated, update To Bank to absorb the difference.
                // When recalcFromBank is true (To Bank is updated), leave To Cash untouched.
                if (!recalcFromBank)
                {
                    decimal.TryParse(txtToCash.Text, out decimal toCash);
                    decimal toBank = netTotal - toCash;
                    txtToBank.Text = toBank.ToString("0.00");
                }

                // Always calculate the remaining balance to Credit.
                decimal.TryParse(txtToBank.Text, out decimal toBankValue);
                decimal.TryParse(txtToCash.Text, out decimal toCashValue);

                decimal toCredit = netTotal - toBankValue - toCashValue;
                txtToCredit.Text = toCredit.ToString("0.00");
            }
            finally
            {
                _isRecalculatingSettlement = false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // BUTTON HANDLERS
        // ════════════════════════════════════════════════════════════════════
        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            _saveMode = "Save";
            _editingPurchaseNo = 0;

            ClearAll();
            ToggleFields(true);

            txtPurchaseNo.Text = _repo.GetNextPurchaseNo().ToString();
            btnSave.Content = "_Save";
            btnDelete.IsEnabled = false;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                dpPurchaseDate.Focus()));
        }

        /// <summary>
        /// Validates and saves/updates whatever is currently on the form, in
        /// the same single-method shape as SalesEntryView.BtnSave_Click — no
        /// separate TrySaveCurrent() indirection. OnEditRequested below calls
        /// straight into this handler (mirroring how Sales' OnEditRequested
        /// calls BtnSave_Click) and re-checks HasUnsavedWork() afterwards to
        /// tell whether the save actually went through.
        /// </summary>
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !PermissionGate.RequireEdit()) return;
            if (!ValidateForm()) return;

            var summary = BuildSummaryFromUI();

            var items = _itemRows
                .Where(r => !string.IsNullOrWhiteSpace(r.ItemCode) && r.Quantity > 0)
                .ToList();

            if (!items.Any())
            {
                MessageBox.Show("Please enter at least one item in the bill.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var expenses = _expenseRows
                .Where(r => r.AcHeadCode > 0)
                .ToList();

            bool updateRate = false;

            try
            {
                if (_saveMode == "Save")
                {
                    _repo.Save(summary, items, expenses, updateRate, currentUser: "Admin");
                    MessageBox.Show("Purchase saved successfully.", "Saved",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var ans = MessageBox.Show(
                        "Do you want to update the Purchase Rate in item master?",
                        "Update Rate", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    updateRate = (ans == MessageBoxResult.Yes);

                    _repo.Update(summary, items, expenses, updateRate,
                        currentUser: "Admin",
                        originalCreatedBy: _originalCreatedBy,
                        originalCreatedDate: _originalCreatedDate);

                    MessageBox.Show("Purchase updated successfully.", "Updated",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _saveMode = "Save";
                _editingPurchaseNo = 0;
                ClearAll();
                ToggleFields(false);
                btnSave.Content = "_Save";
                btnDelete.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving purchase:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!PermissionGate.RequireDelete()) return;
            if (_editingPurchaseNo == 0) return;

            var result = MessageBox.Show(
                $"This will permanently delete Purchase No. {_editingPurchaseNo}.\nAre you sure?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                _repo.Delete(_editingPurchaseNo);
                MessageBox.Show("Purchase deleted.", "Deleted",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                _saveMode = "Save";
                _editingPurchaseNo = 0;
                ClearAll();
                ToggleFields(false);
                btnSave.Content = "_Save";
                btnDelete.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error deleting record:\n" + ex.Message,
                    "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // SEARCH OVERLAY
        // ════════════════════════════════════════════════════════════════════
        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allBills = _repo.GetAll();
            var config = new SearchConfig
            {
                Title = "Search Purchase Bills",
                SearchProperties = new List<string> { "PurchaseNo", "VendorName", "BillNo", "Mobile" },

                ShowDateFilter = true,
                DateFilterProperty = "PurchaseDate",

                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "Purch. No",   BindingPath = "PurchaseNo",    Width = "100" },
                    new SearchColumn { Header = "Date",        BindingPath = "PurchaseDate",  Width = "150" , StringFormat = "{0:dd/MM/yyyy}"},
                    new SearchColumn { Header = "Bill No",     BindingPath = "BillNo",        Width = "100" },
                    new SearchColumn { Header = "Vendor",      BindingPath = "VendorName",    Width = "*" },
                    new SearchColumn { Header = "Mobile",      BindingPath = "Mobile",        Width = "150"},
                    new SearchColumn { Header = "Net Total",   BindingPath = "NetTotal",      Width = "100" },
                },
                TargetOnSelect = btnSave
            };
            SearchOverlay.LoadData(allBills, config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is PurchaseSummary selected)
                LoadBillForEdit(selected.PurchaseNo);
        }

        private void SearchOverlay_Closed(object sender, EventArgs e) => btnDisplay.Focus();

        /// <summary>
        /// Called via EntryEditNavigator when another view (e.g. a report)
        /// asks this form to load a specific bill for editing. Confirms with
        /// the user before discarding any unsaved work already on the form.
        /// Mirrors SalesEntryView.OnEditRequested — calls straight into
        /// BtnSave_Click rather than a separate TrySaveCurrent() helper, then
        /// re-checks HasUnsavedWork() to tell whether the save actually went
        /// through (ValidateForm failing, or a save/update exception, shows
        /// its own MessageBox and leaves the form exactly as it was).
        /// </summary>
        private void OnEditRequested(int purchaseNo)
        {
            // Already showing exactly this bill — nothing to switch.
            if (_saveMode == "Update" && _editingPurchaseNo == purchaseNo)
                return;

            if (HasUnsavedWork())
            {
                string currentLabel = _editingPurchaseNo != 0
                    ? $"Purchase Bill No. {_editingPurchaseNo}"
                    : "the new purchase bill you're entering";

                var result = MessageBox.Show(
                    $"You have unsaved changes to {currentLabel}.\n\n" +
                    $"Save it before switching to Purchase Bill No. {purchaseNo}?",
                    "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
                    return; // stay on the current bill, ignore this edit request

                if (result == MessageBoxResult.Yes)
                {
                    BtnSave_Click(this, new RoutedEventArgs());
                    // ValidateForm failing (or a save/update exception) shows
                    // its own MessageBox and leaves the form exactly as it
                    // was — still unsaved. Re-check rather than assuming
                    // success, so we don't discard the operator's work.
                    if (HasUnsavedWork())
                        return;
                }
                // MessageBoxResult.No falls through and discards the unsaved changes.
            }

            LoadBillForEdit(purchaseNo);
        }

        /// <summary>
        /// True if the form currently has entry work that hasn't been saved:
        /// either an existing bill loaded for edit, or a fresh bill with at
        /// least one item row or a bill number typed in. Only meaningful
        /// while the form is actually active (ToggleFields(true)) — an idle
        /// form with disabled fields has nothing to lose.
        /// </summary>
        private bool HasUnsavedWork()
        {
            if (!btnSave.IsEnabled) return false;

            return _editingPurchaseNo != 0
                || _itemRows.Any(r => !string.IsNullOrWhiteSpace(r.ItemCode))
                || !string.IsNullOrWhiteSpace(txtBillNo.Text);
        }

        public void LoadBillForEdit(int purchaseNo)
        {
            _isLoadingBillForEdit = true;
            try
            {
                var summary = _repo.GetSummaryByNo(purchaseNo);
                if (summary == null) return;

                var items = _repo.GetItemsByPurchaseNo(purchaseNo);
                var expenses = _repo.GetExpensesByPurchaseNo(purchaseNo);
                _repo.ResolveAcHeadNames(expenses);

                _originalCreatedBy = summary.CreatedBy;
                _originalCreatedDate = summary.CreatedDate;
                _editingPurchaseNo = purchaseNo;
                _saveMode = "Update";

                bool canEdit = PermissionGate.CanEdit;
                ToggleFields(canEdit);

                txtPurchaseNo.Text = summary.PurchaseNo.ToString();
                dpPurchaseDate.SelectedDate = summary.PurchaseDate;
                txtBillNo.Text = summary.BillNo;
                dpBillDate.SelectedDate = summary.BillDate;
                cmbPurchaseMode.SelectedIndex = summary.PurchaseMode;

                if (summary.PurchaseMode == 0)
                    txtCashVendorName.Text = summary.VendorName;
                else
                    cmbVendorName.SelectedValue = summary.VendorCode;

                txtAddress1.Text = summary.Address1;
                txtAddress2.Text = summary.Address2;
                txtGstNo.Text = summary.GstNo;
                txtPhone.Text = summary.Phone;
                txtMobile.Text = summary.Mobile;
                txtState.Text = summary.State;

                _itemRows.Clear();
                foreach (var it in items) _itemRows.Add(it);

                _expenseRows.Clear();
                foreach (var ex in expenses) _expenseRows.Add(ex);

                txtCashDiscount.Text = summary.CashDiscount.ToString("0.00");
                txtRoundedAmount.Text = summary.RoundedAmount.ToString("0.00");

                // Loaded verbatim from the saved bill — with _isLoadingBillForEdit
                // still true, none of these Text assignments (nor the item/expense
                // adds above) trigger a settlement recalculation that would
                // overwrite them.
                txtToBank.Text = summary.ToCardAmount.ToString("0.00");
                cmbBankHead.SelectedValue = summary.BankHeadCode > 0 ? (object)summary.BankHeadCode : null;
                txtToCash.Text = summary.ToCashAmount.ToString("0.00");
                txtToCredit.Text = summary.ToCreditAmount.ToString("0.00");

                // The bill already has its own saved bank/cash/credit split —
                // don't let RecalcTotals() below auto-overwrite To Bank with
                // the net total once loading finishes and normal edits resume.
                _toBankManuallyEdited = true;

                // Refreshes the Amount/Tax/Grand/Net Total labels only — still
                // guarded by _isLoadingBillForEdit, so To Bank/To Cash/To Credit
                // are left exactly as just loaded.
                RecalcTotals();

                btnSave.Content = "_Update";
                btnSave.IsEnabled = canEdit;
                btnDelete.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading bill:\n" + ex.Message,
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLoadingBillForEdit = false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // FORM VALIDATION
        // ════════════════════════════════════════════════════════════════════
        private bool ValidateForm()
        {
            if (dpPurchaseDate.SelectedDate == null)
            {
                MessageBox.Show("Please select a purchase date.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                dpPurchaseDate.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtBillNo.Text))
            {
                MessageBox.Show("Please enter the Bill Number.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtBillNo.Focus();
                return false;
            }

            if (dpBillDate.SelectedDate == null)
            {
                MessageBox.Show("Please select a bill date.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                dpBillDate.Focus();
                return false;
            }

            decimal.TryParse(txtToBank.Text, out decimal toBank);
            if (toBank != 0 && cmbBankHead.SelectedItem == null)
            {
                MessageBox.Show("A bank account must be selected when there is a bank payment amount.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                cmbBankHead.Focus();
                return false;
            }

            if (cmbPurchaseMode.SelectedIndex == 1)  // Credit
            {
                if (cmbVendorName.SelectedItem == null)
                {
                    MessageBox.Show("Please select a vendor for credit purchase.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    cmbVendorName.Focus();
                    return false;
                }
            }

            // Cash mode with a remaining credit balance means part of the bill
            // is going on the vendor's account — that only makes sense for a
            // registered vendor in Credit mode.
            if (cmbPurchaseMode.SelectedIndex == 0)  // Cash
            {
                decimal.TryParse(txtToCredit.Text, out decimal toCredit);
                if (toCredit != 0)
                {
                    var result = MessageBox.Show(
                        $"There is a credit balance of {toCredit:N2} in settlement.\n\n" +
                        "Cash purchases cannot have a credit balance\n" +
                        "Switch to Credit mode?",
                        "Mode Mismatch!",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        cmbPurchaseMode.SelectedIndex = 1;
                        cmbVendorName.Focus();
                    }
                    return false;
                }
            }

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        // BUILD SUMMARY OBJECT FROM UI
        // ════════════════════════════════════════════════════════════════════
        private PurchaseSummary BuildSummaryFromUI()
        {
            decimal.TryParse(txtCashDiscount.Text, out decimal cashDisc);
            decimal.TryParse(txtRoundedAmount.Text, out decimal rounded);
            decimal.TryParse(txtToBank.Text, out decimal toCard);
            decimal.TryParse(txtToCash.Text, out decimal toCash);
            decimal.TryParse(txtToCredit.Text, out decimal toCredit);

            int bankHeadCode = 0;
            string bankHeadName = "";
            if (cmbBankHead.SelectedItem is AcHeadLookup bank)
            {
                bankHeadCode = bank.AcHeadCode;
                bankHeadName = bank.AcHeadName;
            }

            decimal amountTotal = _itemRows.Sum(r => r.Price);
            decimal taxTotal = _itemRows.Sum(r => r.TaxAmount);
            decimal grandTotal = amountTotal + taxTotal;
            decimal netTotal = grandTotal + rounded - cashDisc;

            int vendorCode = 0;
            string vendorName;

            if (cmbPurchaseMode.SelectedIndex == 1 && cmbVendorName.SelectedItem is VendorCustomer v)
            {
                vendorCode = v.CustomerCode;
                vendorName = v.CustomerName;
            }
            else
            {
                vendorName = txtCashVendorName.Text.Trim();
            }

            return new PurchaseSummary
            {
                PurchaseNo = int.Parse(txtPurchaseNo.Text),
                PurchaseDate = dpPurchaseDate.SelectedDate ?? DateTime.Today,
                BillNo = txtBillNo.Text.Trim(),
                BillDate = dpBillDate.SelectedDate ?? DateTime.Today,
                PurchaseMode = cmbPurchaseMode.SelectedIndex,
                VendorCode = vendorCode,
                VendorName = vendorName,
                Address1 = txtAddress1.Text.Trim(),
                Address2 = txtAddress2.Text.Trim(),
                GstNo = txtGstNo.Text.Trim(),
                Phone = txtPhone.Text.Trim(),
                Mobile = txtMobile.Text.Trim(),
                State = txtState.Text.Trim(),

                PriceAmount = amountTotal,
                TaxTotal = taxTotal,
                GrandTotal = grandTotal,
                NetTotal = netTotal,
                CashDiscount = cashDisc,
                RoundedAmount = rounded,
                ToCardAmount = toCard,
                BankHeadCode = bankHeadCode,
                BankHeadName = bankHeadName,
                ToCashAmount = toCash,
                ToCreditAmount = toCredit,
            };
        }
    }
}