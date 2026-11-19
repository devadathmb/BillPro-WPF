using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BillPro.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;
// Alias to resolve ambiguity with Syncfusion.UI.Xaml.Grid.SelectionChangedEventArgs
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace BillPro.Views
{
    public partial class SalesEntryView : UserControl, IDisposable
    {
        // ── Repositories ─────────────────────────────────────────────────
        private readonly SalesRepository _repo = new();
        private readonly SalesSeriesRepository _seriesRepo = new();
        private readonly VendorCustomerRepository _customerRepo = new();
        private readonly CompanyRepository _companyRepo = new();

        // ── Lookup data (loaded at startup / on refresh) ────────────────
        public ObservableCollection<SalesItemLookup> AllBatchLookups { get; } = new();
        public ObservableCollection<VendorCustomerMaster> AllCustomersList { get; } = new();
        public ObservableCollection<AcHeadLookup> AllBankHeadsList { get; } = new();
        private List<SalesSeriesMaster> _allSeries = new();

        // Remembers the series picked on the last bill in this session, so
        // "New" retains it instead of always snapping back to the default.
        // Stays null until the first bill is started, at which point
        // BtnNew_Click falls back to the configured default (or first row).
        private string _lastUsedPrefix = null;

        // ── Grid data ────────────────────────────────────────────────────
        private ObservableCollection<SalesLine> _itemRows = new();

        // ── State ────────────────────────────────────────────────────────
        private string _saveMode = "Save";        // "Save" | "Update"
        private string _editingPrefix = null;
        private int _editingBillNo = 0;
        private bool _isCalculatingEntry = false;
        private bool _isCancelledBillLoaded = false;

        // Settlement (By Cash / By Bank / By Credit) cascade state — same
        // pattern as PurchaseEntryView: _toBankManuallyEdited stops the
        // default-to-net-total behavior once the user edits it themselves;
        // _isRecalculatingSettlement guards against re-entrancy since the
        // three fields recalculate from each other.
        private bool _toBankManuallyEdited = false;
        private bool _isRecalculatingSettlement = false;

        // Set for the duration of LoadBillForEdit(). While true, RecalcTotals()
        // still refreshes the Gross/Discount/Tax/Net Total labels (so they're
        // correct as items stream in), but leaves By Cash / By Bank / By Credit
        // completely alone — those are populated verbatim from the saved bill.
        // Without this, every _itemRows.Add() during the load fires
        // RecalcTotals() -> RecalcSettlement(), which recomputes By Credit
        // from whatever partial totals exist at that instant and stomps the
        // saved settlement split before the real values are even assigned.
        // Also skips CmbPrefix_SelectionChanged's bill-number-preview logic
        // during the load — see that handler for why.
        private bool _isLoadingBillForEdit = false;

        // Quantity captured just before an in-grid cell edit begins, so
        // SfItems_CurrentCellEndEdit can adjust the batch's live stock by
        // only the delta if the operator changes Quantity directly in the
        // grid (rather than through the entry line + Add Item), and can
        // roll the cell back if the committed value is invalid (<= 0).
        private decimal _qtyBeforeGridEdit = 0;
        private string _lastSelectedBankHeadCode = null;

        public SalesEntryView()
        {
            InitializeComponent();

            DataContext = this;

            this.IsVisibleChanged += SalesEntryView_IsVisibleChanged;

            _repo.InitializeDatabase();
            _companyRepo.InitializeDatabase();

            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;

            sfItems.ItemsSource = _itemRows;
            _itemRows.CollectionChanged += (_, __) => RecalcTotals();

            // Lets SalesReportView / SalesSettlementReportView (or any other
            // report) ask this view to load a specific bill for editing, via
            // right-click "Edit This Bill", without either view needing a
            // direct reference to the other. Subscribed once for the
            // lifetime of this instance — see Dispose() for why we don't
            // unsubscribe on Unloaded.
            EntryEditNavigator.Subscribe<(string Prefix, int BillNo)>(EntryChannels.SalesEntry, OnEditRequested);

            cmbEntryItem.ItemsSource = AllBatchLookups;
            cmbCustomer.ItemsSource = AllCustomersList;
            cmbBankHead.ItemsSource = AllBankHeadsList;

            LoadLookups();
        }

        private void SalesEntryView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
                RefreshMasterData();
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
                _allSeries = _seriesRepo.GetActive();
                cmbPrefix.ItemsSource = _allSeries;

                ReloadBatchLookups();

                AllCustomersList.Clear();
                foreach (var c in _customerRepo.GetAll())
                    AllCustomersList.Add(c);

                AllBankHeadsList.Clear();
                foreach (var h in _repo.GetBankAcHeads())
                    AllBankHeadsList.Add(h);

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

        private void RefreshMasterData()
        {
            try
            {
                var currentBatch = cmbEntryItem.SelectedValue;
                var currentCustomer = cmbCustomer.SelectedValue;
                var currentBankHead = cmbBankHead.SelectedValue;

                // cmbPrefix wasn't being preserved/restored here like the
                // combos above — so any tab-visibility flip (e.g. opening
                // this view from a report via OpenOrActivateTitle, right
                // after LoadBillForEdit already set the selection) silently
                // dropped it: reassigning ItemsSource below always clears
                // the current selection since the old SelectedItem instance
                // isn't in the new collection, even when a value-equal item
                // exists. That in turn fired CmbPrefix_SelectionChanged and
                // blanked txtBillNoPreview too.
                var currentPrefix = cmbPrefix.SelectedValue as string;

                _allSeries = _seriesRepo.GetActive();

                // If a bill is currently loaded whose series isn't (or is no
                // longer) active, keep it in the list — same fallback
                // LoadBillForEdit uses — so refreshing master data while
                // that bill is open doesn't blank the series out from under it.
                var seriesForCombo = _allSeries;
                if (currentPrefix != null && !_allSeries.Any(s => s.Prefix == currentPrefix))
                {
                    seriesForCombo = new List<SalesSeriesMaster>(_allSeries)
                    {
                        new SalesSeriesMaster { Prefix = currentPrefix }
                    };
                }
                cmbPrefix.ItemsSource = seriesForCombo;

                ReloadBatchLookups();

                AllCustomersList.Clear();
                foreach (var c in _customerRepo.GetAll())
                    AllCustomersList.Add(c);

                AllBankHeadsList.Clear();
                foreach (var h in _repo.GetBankAcHeads())
                    AllBankHeadsList.Add(h);

                if (currentBatch != null) cmbEntryItem.SelectedValue = currentBatch;
                if (currentCustomer != null) cmbCustomer.SelectedValue = currentCustomer;
                if (currentBankHead != null) cmbBankHead.SelectedValue = currentBankHead;
                // Restoring this can still fire CmbPrefix_SelectionChanged,
                // but that handler now no-ops while _saveMode == "Update" —
                // txtBillNoPreview/lblSeriesCategory are left untouched.
                if (currentPrefix != null) cmbPrefix.SelectedValue = currentPrefix;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error refreshing data:\n" + ex.Message,
                    "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Re-pulls the batch lookup list, honoring the 0/negative-stock checkbox.</summary>
        private void ReloadBatchLookups()
        {
            bool allowZero = chkAllowZeroStock.IsChecked == true;
            var batches = _repo.GetSalesBatchLookups(allowZero);

            AllBatchLookups.Clear();
            foreach (var b in batches)
                AllBatchLookups.Add(b);

        }

        private void ChkAllowZeroStock_CheckedChanged(object sender, RoutedEventArgs e) => ReloadBatchLookups();

        // ════════════════════════════════════════════════════════════════════
        // UI STATE HELPERS
        // ════════════════════════════════════════════════════════════════════
        private void ToggleFields(bool enabled)
        {
            cmbPrefix.IsEnabled = enabled;
            dpBillDate.IsEnabled = enabled;
            cmbBillType.IsEnabled = enabled;
            chkAllowZeroStock.IsEnabled = enabled;

            if (enabled) ApplyCustomerMode();
            else
            {
                txtCashCustomerName.IsEnabled = false;
                cmbCustomer.IsEnabled = false;
            }

            txtCustomerAddress.IsEnabled = enabled;
            txtCustomerGstNo.IsEnabled = enabled;
            txtCustomerPhone.IsEnabled = enabled;
            txtState.IsEnabled = enabled;
            txtStateCode.IsEnabled = enabled;

            sfItems.IsEnabled = enabled;
            ToggleItemEntryLine(enabled);

            txtTransporterName.IsEnabled = enabled;
            txtVehicleNo.IsEnabled = enabled;
            txtPlaceOfDelivery.IsEnabled = enabled;
            dpDateOfDelivery.IsEnabled = enabled;

            txtDiscountPercent.IsEnabled = enabled;
            txtCashDiscount.IsEnabled = enabled;
            txtRoundOff.IsEnabled = enabled;

            txtByCash.IsEnabled = enabled;
            txtByBank.IsEnabled = enabled;
            cmbBankHead.IsEnabled = enabled;
            txtByCredit.IsEnabled = enabled;

            btnSave.IsEnabled = enabled;

            pnlCancelledWatermark.Visibility = Visibility.Collapsed;
        }

        private void ToggleItemEntryLine(bool enabled)
        {
            cmbEntryItem.IsEnabled = enabled;
            txtEntryRate.IsEnabled = enabled;
            txtEntryQty.IsEnabled = enabled;
            txtEntryDiscount.IsEnabled = enabled;
            txtEntryDiscountAmount.IsEnabled = enabled;
            txtEntryTaxable.IsEnabled = enabled;
            txtEntryTaxRate.IsEnabled = enabled;
            txtEntryTaxAmount.IsEnabled = enabled;
            btnAddItem.IsEnabled = enabled;
        }

        private void ClearAll()
        {
            dpBillDate.SelectedDate = DateTime.Today;
            cmbBillType.SelectedIndex = 0;
            chkAllowZeroStock.IsChecked = false;

            ClearCustomerDetails();

            _itemRows.Clear();
            ClearItemEntryLine();

            txtTransporterName.Text = "";
            txtVehicleNo.Text = "";
            txtPlaceOfDelivery.Text = "";
            dpDateOfDelivery.SelectedDate = null;

            txtDiscountPercent.Text = "0";
            txtCashDiscount.Text = "0";
            txtRoundOff.Text = "0";

            txtByCash.Text = "0";
            txtByBank.Text = "0";
            txtByCredit.Text = "0";
            _toBankManuallyEdited = false;
            if (cmbBankHead.SelectedValue != null)
                _lastSelectedBankHeadCode = cmbBankHead.SelectedValue.ToString();

            RecalcTotals();
        }

        private void ClearCustomerDetails()
        {
            txtCashCustomerName.Text = "";
            cmbCustomer.SelectedIndex = -1;
            cmbCustomer.Text = "";
            txtCustomerAddress.Text = "";
            txtCustomerGstNo.Text = "";
            txtCustomerPhone.Text = "";
            txtState.Text = "";
            txtStateCode.Text = "";
        }

        private void ClearItemEntryLine()
        {
            cmbEntryItem.SelectionChanged -= CmbEntryItem_SelectionChanged;
            cmbEntryItem.SelectedIndex = -1;
            cmbEntryItem.Text = "";
            cmbEntryItem.SelectionChanged += CmbEntryItem_SelectionChanged;

            txtEntryHsn.Text = "";
            txtEntryMrp.Text = "";
            txtEntryRate.Text = "";
            txtEntryQty.Text = "";
            txtEntryDiscount.Text = "0.00";
            txtEntryDiscountAmount.Text = "0.00";
            txtEntryTaxable.Text = "";
            txtEntryTaxRate.Text = "";
            txtEntryTaxAmount.Text = "0.00";
            txtEntryTotal.Text = "";

            lblStockWarning.Visibility = Visibility.Collapsed;
            lblAvailableStock.Text = "";
        }

        private void TxtCashCustomerName_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                cmbCashCustomerLookup.SelectedIndex = -1;
                cmbCashCustomerLookup.Text = "";
                popupCustomerLookup.IsOpen = true;
                e.Handled = true;
            }
        }

        private void PopupCustomerLookup_Opened(object sender, EventArgs e)
        {
            // Focus the combobox as soon as the popup opens so typing works immediately
            Dispatcher.BeginInvoke(new Action(() =>
            {
                cmbCashCustomerLookup.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CmbCashCustomerLookup_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbCashCustomerLookup.SelectedItem is BillPro.Models.VendorCustomerMaster selectedCustomer)
            {
                txtCashCustomerName.Text = selectedCustomer.CustomerName;
                FillCustomerDetails(selectedCustomer); // Utilizes your existing method

                popupCustomerLookup.IsOpen = false;

                // Move focus to the next logical field after selection
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtCustomerAddress.Focus();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void CmbCashCustomerLookup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                popupCustomerLookup.IsOpen = false;
                txtCashCustomerName.Focus();
                e.Handled = true;
            }
        }

        private void ApplyCustomerMode()
        {
            bool isCredit = cmbBillType.SelectedIndex == 1;
            gridCashCustomer.Visibility = isCredit ? Visibility.Collapsed : Visibility.Visible;
            cmbCustomer.Visibility = isCredit ? Visibility.Visible : Visibility.Collapsed;
            txtCashCustomerName.IsEnabled = !isCredit;
            cmbCustomer.IsEnabled = isCredit;
        }

        // ════════════════════════════════════════════════════════════════════
        // HEADER COMBO EVENTS
        // ════════════════════════════════════════════════════════════════════
        private void CmbPrefix_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingBillForEdit) return;

            // The series is permanently fixed once a bill exists (it's baked
            // into the already-claimed FormattedBillNo) — never recompute or
            // clear the bill-number preview for a bill being edited, even if
            // something reassigns cmbPrefix's ItemsSource/selection later
            // (e.g. RefreshMasterData firing on tab visibility while this
            // bill is still open) and this event fires again outside the
            // guarded load.
            if (_saveMode == "Update") return;

            if (cmbPrefix.SelectedValue is not string prefix)
            {
                txtBillNoPreview.Text = "";
                lblSeriesCategory.Text = "—";
                return;
            }

            RefreshBillNoPreview(prefix);
        }

        // Pulled out of CmbPrefix_SelectionChanged so it can be called
        // explicitly (e.g. from BtnNew_Click) rather than only relying on
        // the SelectionChanged event — WPF's ComboBox does NOT raise that
        // event when SelectedItem is set to a value equal to what's
        // already selected. That was the "New doesn't update the bill
        // number" bug: after displaying an existing bill, its series was
        // already the one shown in cmbPrefix, so re-selecting the same
        // series on New silently did nothing, leaving the old bill's
        // number in the preview instead of the true next number.
        private void RefreshBillNoPreview(string prefix)
        {
            if (cmbPrefix.SelectedItem is SalesSeriesMaster series)
                lblSeriesCategory.Text = $"{series.CustomerType} / {series.TransactionType}";
            else
                lblSeriesCategory.Text = "—";

            _lastUsedPrefix = prefix;

            try
            {
                var (_, formatted) = _seriesRepo.PreviewNextBillNo(prefix);
                txtBillNoPreview.Text = formatted;
            }
            catch (Exception ex)
            {
                txtBillNoPreview.Text = "";
                MessageBox.Show(ex.Message, "Series Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CmbBillType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyCustomerMode();
            ClearCustomerDetails();
        }

        private void CmbCustomer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbCustomer.SelectedItem is VendorCustomerMaster c)
                FillCustomerDetails(c);
        }

        private void FillCustomerDetails(VendorCustomerMaster c)
        {
            txtCustomerAddress.Text = string.Join(", ",
                new[] { c.Address1, c.Address2, c.Address3, c.Place, c.Pin }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            txtCustomerGstNo.Text = c.GstNo ?? "";
            txtCustomerPhone.Text = !string.IsNullOrWhiteSpace(c.Mobile) ? c.Mobile : (c.Phone ?? "");
        }

        // ════════════════════════════════════════════════════════════════════
        // ITEM ENTRY LINE
        // ════════════════════════════════════════════════════════════════════
        private void CmbEntryItem_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not SalesItemLookup selected) return;

            FillItemEntryLine(selected);
            Dispatcher.BeginInvoke(new Action(() => txtEntryQty.Focus()), DispatcherPriority.Input);
        }

        private void FillItemEntryLine(SalesItemLookup item)
        {
            txtEntryHsn.Text = item.HsnCode;
            txtEntryMrp.Text = ((decimal)item.Mrp).ToString("N2");
            txtEntryRate.Text = ((decimal)item.SalesRate).ToString("N2");
            txtEntryDiscount.Text = "0.00";
            txtEntryTaxRate.Text = item.TaxRate.ToString("N2");
            txtEntryQty.Text = "";

            UpdateAvailableStockLabel(item);

            if (item.IsLowOrNegativeStock && !item.IsNewBatch)
            {
                lblStockWarning.Text = $"⚠ This batch currently has {item.CurrentStock:N3} in stock.";
                lblStockWarning.Visibility = Visibility.Visible;
            }
            else if (item.IsNewBatch)
            {
                lblStockWarning.Text = "⚠ Never purchased before — a new batch will be created on save.";
                lblStockWarning.Visibility = Visibility.Visible;
            }
            else
            {
                lblStockWarning.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Updates the small "Available: n" readout under the Quantity field
        /// for whichever batch is currently selected in the entry line.
        /// Reflects live, in-session stock — see CommitItemEntry / the
        /// delete handler, which decrement/restore item.CurrentStock as
        /// lines are added to or removed from the bill.
        /// </summary>
        private void UpdateAvailableStockLabel(SalesItemLookup item)
        {
            if (item == null)
            {
                lblAvailableStock.Text = "";
                return;
            }

            if (item.IsNewBatch)
            {
                lblAvailableStock.Text = "Available: — (new batch)";
                lblAvailableStock.Foreground = (Brush)new BrushConverter().ConvertFrom("#6B7280");
                return;
            }

            lblAvailableStock.Text = $"Available: {item.CurrentStock:N3}";
            lblAvailableStock.Foreground = item.CurrentStock <= 0
                ? (Brush)new BrushConverter().ConvertFrom("#DC2626")
                : (Brush)new BrushConverter().ConvertFrom("#6B7280");
        }

        private void BtnAddItem_Click(object sender, RoutedEventArgs e) => CommitItemEntry();

        private void CommitItemEntry()
        {
            if (cmbEntryItem.SelectedItem is not SalesItemLookup selected)
            {
                MessageBox.Show("Please select a valid item/batch from the list.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                cmbEntryItem.Focus();
                return;
            }

            if (!decimal.TryParse(txtEntryQty.Text, out decimal qty) || qty <= 0)
            {
                MessageBox.Show("Please enter a valid quantity greater than zero.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtEntryQty.Focus();
                return;
            }

            // Insufficient-stock confirmation — never blocks the sale outright
            // (walk-in retail sometimes has to sell below zero), but makes the
            // operator explicitly confirm rather than sliding past it. Default
            // button is Yes since confirming-and-continuing is the common case.
            // Skipped entirely when "allow 0/negative stock" is already
            // checked — that checkbox is itself the operator's blanket
            // permission, so a popup on every such line would be redundant.
            if (!selected.IsNewBatch && qty > (decimal)selected.CurrentStock && chkAllowZeroStock.IsChecked != true)
            {
                var confirm = MessageBox.Show(
                    $"Only {selected.CurrentStock:N3} in stock for this batch.\n\nAdd {qty:N3} anyway?",
                    "Insufficient Stock", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);

                if (confirm != MessageBoxResult.Yes)
                {
                    txtEntryQty.Focus();
                    txtEntryQty.SelectAll();
                    return;
                }
            }

            decimal.TryParse(txtEntryRate.Text, out decimal rate);
            decimal.TryParse(txtEntryDiscount.Text, out decimal discRate);
            decimal.TryParse(txtEntryDiscountAmount.Text, out decimal discAmt);
            decimal.TryParse(txtEntryTaxable.Text, out decimal taxable);
            decimal.TryParse(txtEntryTaxRate.Text, out decimal taxRate);
            decimal.TryParse(txtEntryTaxAmount.Text, out decimal taxAmt);
            decimal.TryParse(txtEntryTotal.Text, out decimal total);

            var row = new SalesLine
            {
                ItemCode = selected.ItemCode,
                ItemName = selected.ItemName,
                HsnCode = selected.HsnCode,
                BatchId = selected.BatchId,
                BatchNumber = selected.IsNewBatch ? "(New)" : selected.BatchNumber,
                Mrp = (decimal)selected.Mrp,
                Rate = rate,
                UnitPrice = ComputeUnitPrice(rate, taxRate),
                Quantity = qty,
                DiscountRate = discRate,
                Discount = discAmt,
                TaxableAmount = taxable,
                TaxRate = taxRate,
                TaxAmount = taxAmt,
                Total = total,
                IsLowOrNegativeStock = selected.IsLowOrNegativeStock
            };

            _itemRows.Add(row);
            RecalcTotals();

            // Reflect the sale against the batch's stock immediately — not
            // just on Save. Keeps the combo/group header and the
            // available-stock label accurate if the same batch is picked
            // again later in this bill. New-batch synthetic rows (BatchId
            // 0) don't have a real stock figure to decrement.
            if (!selected.IsNewBatch)
                selected.CurrentStock -= (double)qty;

            // Scroll the new row into view — without this, adding items past
            // what fits on screen leaves the user staring at the same
            // scrolled position with no visual confirmation the row was added.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                int rowIndex = sfItems.ResolveToRowIndex(row);
                if (rowIndex >= 0)
                    sfItems.ScrollInView(new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIndex, 0));
            }), DispatcherPriority.Loaded);

            ClearItemEntryLine();
            Dispatcher.BeginInvoke(new Action(() => cmbEntryItem.Focus()), DispatcherPriority.Input);
        }

        /// <summary>
        /// Rate / Qty / Discount / Tax cascade for the entry line — mirrors
        /// Purchase Entry's calc chain, just with "Taxable" standing in for
        /// Purchase's "Amount" (same thing: gross minus discount).
        /// </summary>
        private void EntryCalculation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isCalculatingEntry) return;
            if (sender is not TextBox tb) return;

            decimal rate = decimal.TryParse(txtEntryRate.Text, out var r) ? r : 0;
            decimal qty = decimal.TryParse(txtEntryQty.Text, out var q) ? q : 0;
            decimal discPercent = decimal.TryParse(txtEntryDiscount.Text, out var dp) ? dp : 0;
            decimal discAmt = decimal.TryParse(txtEntryDiscountAmount.Text, out var da) ? da : 0;
            decimal taxable = decimal.TryParse(txtEntryTaxable.Text, out var tb2) ? tb2 : 0;
            decimal taxRate = decimal.TryParse(txtEntryTaxRate.Text, out var tr) ? tr : 0;
            decimal taxAmt = decimal.TryParse(txtEntryTaxAmount.Text, out var ta) ? ta : 0;

            _isCalculatingEntry = true;
            try
            {
                if (tb == txtEntryRate || tb == txtEntryQty || tb == txtEntryDiscount || tb == txtEntryTaxRate)
                {
                    decimal unitPrice = ComputeUnitPrice(rate, taxRate);
                    txtEntryUnitPrice.Text = unitPrice.ToString("0.00");

                    decimal gross = unitPrice * qty;
                    discAmt = Math.Round(gross * (discPercent / 100m), 2);
                    txtEntryDiscountAmount.Text = discAmt.ToString("0.00");

                    taxable = gross - discAmt;
                    txtEntryTaxable.Text = taxable.ToString("0.00");

                    taxAmt = Math.Round(taxable * (taxRate / 100m), 2);
                    txtEntryTaxAmount.Text = taxAmt.ToString("0.00");
                }
                else if (tb == txtEntryDiscountAmount)
                {
                    decimal unitPrice = ComputeUnitPrice(rate, taxRate);
                    txtEntryUnitPrice.Text = unitPrice.ToString("0.00");

                    decimal gross = unitPrice * qty;
                    taxable = gross - discAmt;
                    txtEntryTaxable.Text = taxable.ToString("0.00");

                    taxAmt = Math.Round(taxable * (taxRate / 100m), 2);
                    txtEntryTaxAmount.Text = taxAmt.ToString("0.00");
                }
                else if (tb == txtEntryTaxable)
                {
                    taxAmt = Math.Round(taxable * (taxRate / 100m), 2);
                    txtEntryTaxAmount.Text = taxAmt.ToString("0.00");
                }
                // tb == txtEntryTaxAmount: user overriding tax directly, nothing upstream changes

                decimal total = taxable + taxAmt;
                txtEntryTotal.Text = total.ToString("0.00");
            }
            finally
            {
                _isCalculatingEntry = false;
            }
        }

        /// <summary>
        /// SmartComboBox SelectionChanged inside the Sales items grid edit
        /// template — mirrors Purchase Entry's GridItemCombo_SelectionChanged,
        /// but operates on a batch-level lookup (SalesItemLookup) since Sales
        /// must pin down a specific batch (or the synthetic "new batch" row),
        /// not just an item. Restores stock for whatever batch the row
        /// previously held (if any), fills the row from the newly picked
        /// batch, decrements that batch's live stock the same way
        /// CommitItemEntry does, recalculates line totals, then moves focus
        /// to Quantity for editing.
        /// </summary>
        private void GridEntryItem_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not SalesItemLookup selected)
                return;

            if (sender is not SmartComboBox cmb || cmb.DataContext is not SalesLine row)
                return;

            // Give back stock held against whatever batch this row previously
            // pointed to, before switching it to the newly selected batch.
            RestoreStockForRow(row);

            row.ItemCode = selected.ItemCode;
            row.ItemName = selected.ItemName;
            row.HsnCode = selected.HsnCode;
            row.BatchId = selected.BatchId;
            row.BatchNumber = selected.IsNewBatch ? "(New)" : selected.BatchNumber;
            row.Mrp = (decimal)selected.Mrp;
            row.Rate = (decimal)selected.SalesRate;
            row.TaxRate = (decimal)selected.TaxRate;
            row.UnitPrice = ComputeUnitPrice(row.Rate, row.TaxRate);
            row.IsLowOrNegativeStock = selected.IsLowOrNegativeStock;

            // Re-derive dependent amounts from whatever Quantity/Disc% the row
            // already has (0 the first time a brand-new row is filled in via
            // the grid rather than the entry line).
            decimal gross = row.UnitPrice * row.Quantity;
            row.Discount = Math.Round(gross * (row.DiscountRate / 100m), 2);
            row.TaxableAmount = gross - row.Discount;
            row.TaxAmount = Math.Round(row.TaxableAmount * (row.TaxRate / 100m), 2);
            row.Total = row.TaxableAmount + row.TaxAmount;

            if (!selected.IsNewBatch)
                selected.CurrentStock -= (double)row.Quantity;

            RecalcTotals();

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

        // ════════════════════════════════════════════════════════════════════
        // SFDATAGRID — ITEMS GRID EVENTS
        // ════════════════════════════════════════════════════════════════════
        private void SfItems_CurrentCellBeginEdit(object sender, CurrentCellBeginEditEventArgs e)
        {
            if (sender is not SfDataGrid grid) return;
            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is SalesLine row)
                _qtyBeforeGridEdit = row.Quantity;
        }

        private void SfItems_CurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
        {
            if (sender is not SfDataGrid grid) return;
            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is not SalesLine row) return;

            // Reject a zero/negative quantity committed directly in the grid —
            // roll the cell back to whatever it held before this edit began
            // instead of letting an invalid value flow into totals/stock.
            if (row.Quantity <= 0)
            {
                MessageBox.Show("Quantity must be greater than 0.", "Invalid Quantity",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                row.Quantity = _qtyBeforeGridEdit;
                grid.View.Refresh();
                return;
            }

            // Keep the derived fields consistent if Rate/Qty/Disc%/Tax% were
            // edited directly in the grid after the line was added.
            row.UnitPrice = ComputeUnitPrice(row.Rate, row.TaxRate);
            decimal gross = row.UnitPrice * row.Quantity;
            row.Discount = Math.Round(gross * (row.DiscountRate / 100m), 2);
            row.TaxableAmount = gross - row.Discount;
            row.TaxAmount = Math.Round(row.TaxableAmount * (row.TaxRate / 100m), 2);
            row.Total = row.TaxableAmount + row.TaxAmount;

            // If Quantity itself was the field edited, apply only the delta
            // to the batch's live stock (same idea as CommitItemEntry /
            // RestoreStockForRow, just incremental instead of all-or-nothing).
            if (row.Quantity != _qtyBeforeGridEdit && row.BatchId != 0)
            {
                var lookup = AllBatchLookups.FirstOrDefault(b => b.BatchId == row.BatchId);
                if (lookup != null)
                {
                    decimal delta = row.Quantity - _qtyBeforeGridEdit; // +ve = more sold, -ve = less sold
                    lookup.CurrentStock -= (double)delta;
                }
            }

            RecalcTotals();
        }

        private void BtnDeleteItemRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SalesLine row)
            {
                _itemRows.Remove(row);
                RestoreStockForRow(row);
                RecalcTotals();
            }
        }

        /// <summary>
        /// Adds a deleted (or reduced) line's quantity back onto the matching
        /// batch's live stock figure, mirroring the decrement done in
        /// CommitItemEntry. Looks the batch up by id in AllBatchLookups since
        /// SalesLine itself doesn't hold a reference to the lookup object.
        /// Synthetic "new batch" rows (BatchId 0) have nothing to restore —
        /// there could be several distinct never-purchased items sharing
        /// BatchId 0, so there's no single lookup row to credit anyway.
        /// </summary>
        private void RestoreStockForRow(SalesLine row)
        {
            if (row == null || row.BatchId == 0) return;

            var lookup = AllBatchLookups.FirstOrDefault(b => b.BatchId == row.BatchId);
            if (lookup != null)
                lookup.CurrentStock += (double)row.Quantity;
        }

        // ════════════════════════════════════════════════════════════════════
        // TAX-INCLUSIVE RATE → UNIT PRICE
        // ════════════════════════════════════════════════════════════════════
        // Rate (from the item master / entered on the line) is now the
        // tax-INCLUSIVE price the customer actually pays per unit — tax is
        // no longer added on top of it, it's backed out of it. Every place
        // that used to build Gross/Taxable/Tax/Total off Rate now goes
        // through this first. Deliberately not rounded: rounding to 2dp
        // here before multiplying by Quantity is exactly the kind of
        // precision loss that produces mismatched totals on larger
        // quantities — the *result* of the downstream calc gets rounded to
        // 2dp for money fields as before, this input doesn't.
        private static decimal ComputeUnitPrice(decimal taxInclusiveRate, decimal taxRate) =>
            taxInclusiveRate * 100m / (100m + taxRate);

        // ════════════════════════════════════════════════════════════════════
        // TOTALS
        // ════════════════════════════════════════════════════════════════════
        private void FooterAmount_TextChanged(object sender, TextChangedEventArgs e) => RecalcTotals();

        private void RecalcTotals()
        {
            decimal grossAmount = _itemRows.Sum(r => r.UnitPrice * r.Quantity);
            decimal lineDiscountTotal = _itemRows.Sum(r => r.Discount);
            decimal taxableTotal = _itemRows.Sum(r => r.TaxableAmount);
            decimal taxTotal = _itemRows.Sum(r => r.TaxAmount);

            decimal.TryParse(txtDiscountPercent.Text, out decimal discountPercent);
            decimal.TryParse(txtCashDiscount.Text, out decimal cashDiscount);
            decimal.TryParse(txtRoundOff.Text, out decimal roundOff);

            // Whole-bill discount % still applies on top of whatever the
            // line-level discounts already took off (i.e. against the
            // taxable base, not the raw gross amount) — that part is
            // unchanged. But lineDiscountTotal itself is NOT folded into
            // discountTotal below: each row's TaxableAmount/TaxAmount is
            // already computed net of its own line discount (see
            // AddItem/grid-edit handlers: row.TaxableAmount = gross -
            // row.Discount), so adding lineDiscountTotal here a second
            // time double-subtracted it — Discount Total (and the
            // DiscountAmount saved to SalesSummary) would overstate the
            // real discount, and any reconciliation against
            // Gross - Discount + Tax vs. Net Total would come out short by
            // exactly lineDiscountTotal. Discount Total now only ever
            // reflects the bill-level discount% and Cash Discount — the
            // two adjustments that are NOT already embedded in the line
            // totals.
            decimal billDiscountAmount = Math.Round((grossAmount - lineDiscountTotal) * (discountPercent / 100m), 2);
            decimal discountTotal = billDiscountAmount + cashDiscount;

            // Built from taxableTotal (already net of every line's own
            // discount) rather than grossAmount - discountTotal, so Net
            // Total stays correct now that discountTotal no longer
            // includes lineDiscountTotal.
            decimal netTotal = taxableTotal - billDiscountAmount - cashDiscount + taxTotal + roundOff;

            lblGrossAmount.Text = grossAmount.ToString("N2");
            lblDiscountTotal.Text = discountTotal.ToString("N2");
            lblTaxTotal.Text = taxTotal.ToString("N2");
            lblNetTotal.Text = netTotal.ToString("N2");

            // Default "By Bank" to the full net total — the common case is the
            // whole bill going to the bank/card. Once the user edits it
            // themselves, stop overwriting it here; their split takes over.
            // While a saved bill is loading, By Cash/By Bank/By Credit are
            // being set verbatim from the saved values in LoadBillForEdit —
            // leave them alone here so the CollectionChanged-triggered calls
            // fired while item rows are still streaming in don't stomp them
            // with partial running totals.
            if (_isLoadingBillForEdit) return;

            if (!_toBankManuallyEdited)
                txtByBank.Text = netTotal.ToString("0.00");

            RecalcSettlement(netTotal);
        }

        // ════════════════════════════════════════════════════════════════════
        // SETTLEMENT CASCADE (By Cash / By Bank / By Credit)
        //   Editing By Cash : By Bank = Net Total − By Cash
        //   Editing By Bank : By Cash remains untouched
        //   By Credit (always) = Net Total − By Bank − By Cash
        // ════════════════════════════════════════════════════════════════════
        private void CmbBankHead_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selection is read directly from cmbBankHead in BuildSummaryFromUI;
            // nothing else needs to react when the bank account changes.
        }

        private void SettlementAmount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isRecalculatingSettlement) return;

            if (sender == txtByBank)
            {
                if (txtByBank.IsKeyboardFocused)
                    _toBankManuallyEdited = true;

                RecalcSettlement(recalcFromBank: true);
            }
            else if (sender == txtByCash)
            {
                // User is editing By Cash directly — derive By Bank from it,
                // AND mark the split as manually pinned. Without this, adding
                // another item later would silently reset By Bank back to the
                // full net total and blow away the split the user just set up.
                if (txtByCash.IsKeyboardFocused)
                    _toBankManuallyEdited = true;

                RecalcSettlement(recalcFromBank: false);
            }
        }

        private void RecalcSettlement(decimal? netTotalOverride = null, bool recalcFromBank = true)
        {
            if (_isRecalculatingSettlement) return;
            _isRecalculatingSettlement = true;
            try
            {
                decimal netTotal = netTotalOverride
                    ?? (decimal.TryParse(lblNetTotal.Text, out decimal nt) ? nt : 0);

                if (!recalcFromBank)
                {
                    decimal.TryParse(txtByCash.Text, out decimal byCash);
                    decimal byBank = netTotal - byCash;
                    txtByBank.Text = byBank.ToString("0.00");
                }

                decimal.TryParse(txtByBank.Text, out decimal byBankValue);
                decimal.TryParse(txtByCash.Text, out decimal byCashValue);

                decimal byCredit = netTotal - byBankValue - byCashValue;
                txtByCredit.Text = byCredit.ToString("0.00");
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
            _editingPrefix = null;
            _editingBillNo = 0;
            _isCancelledBillLoaded = false;

            ClearAll();
            ToggleFields(true);

            // Undo whatever LoadBillForEdit may have done to the series
            // combo: restore the real active-series list (dropping any
            // synthetic "old bill's now-inactive series" entry) and
            // re-enable it — a new bill needs the operator to pick a series.
            cmbPrefix.ItemsSource = _allSeries;
            cmbPrefix.IsEnabled = true;

            // Starting a fresh bill discards any unsaved lines from the one
            // just left — re-pull stock from the DB so live decrements made
            // while building that draft don't linger into the new bill.
            ReloadBatchLookups();

            if (_allSeries.Count > 0)
            {
                // Prefer whatever series was used on the last bill this
                // session. Only fall back to the configured default (or the
                // first row) on the very first bill, or if that prefix no
                // longer exists/is inactive (e.g. deactivated mid-session).
                SalesSeriesMaster toSelect = _lastUsedPrefix != null
                    ? _allSeries.FirstOrDefault(s => s.Prefix == _lastUsedPrefix)
                    : null;

                if (toSelect == null)
                {
                    var defaultSeries = _allSeries.FirstOrDefault(s => s.IsDefault == 1);
                    toSelect = defaultSeries ?? _allSeries[0];
                }

                cmbPrefix.SelectedItem = toSelect;

                // Always refresh explicitly — SelectedItem above may be a
                // no-op (same series as before), which would otherwise
                // leave the preview showing whatever bill was last loaded.
                RefreshBillNoPreview(toSelect.Prefix);
            }

            btnSave.Content = "_Save";
            btnCancelBill.IsEnabled = false;
            btnPrint.IsEnabled = false;
            btnExport.IsEnabled = false;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => cmbPrefix.Focus()));
        }

        private bool ValidateForm()
        {
            if (cmbPrefix.SelectedValue is not string)
            {
                MessageBox.Show("Please select a bill series.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbPrefix.Focus();
                return false;
            }

            if (dpBillDate.SelectedDate == null)
            {
                MessageBox.Show("Please select a bill date.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                dpBillDate.Focus();
                return false;
            }

            if (cmbBillType.SelectedIndex == 1 && cmbCustomer.SelectedItem == null)
            {
                MessageBox.Show("Please select a customer for a credit bill.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbCustomer.Focus();
                return false;
            }

            if (_itemRows.Count == 0)
            {
                MessageBox.Show("Please add at least one item to the bill.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            decimal.TryParse(lblNetTotal.Text, out decimal netTotalCheck);
            if (netTotalCheck == 0)
            {
                var zeroTotalConfirm = MessageBox.Show(
                    "The Net Total for this bill is 0.00.\n\n" +
                    "There might be something wrong in the entry! Save anyway?",
                    "Zero Amount Bill", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

                if (zeroTotalConfirm != MessageBoxResult.Yes)
                    return false;
            }

            // Belt-and-braces: every commit path (entry line, grid edit) already
            // rejects <= 0 quantities, but guard the whole collection here too
            // in case a row was constructed some other way.
            if (_itemRows.Any(r => r.Quantity <= 0))
            {
                MessageBox.Show("All items must have a quantity greater than 0.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            decimal.TryParse(txtByBank.Text, out decimal byBank);
            if (byBank != 0 && cmbBankHead.SelectedItem == null)
            {
                MessageBox.Show("A bank account must be selected when there is a bank/card settlement amount.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                cmbBankHead.Focus();
                return false;
            }

            // By Credit is derived as Net Total − By Bank − By Cash, so a
            // Cash bill can end up with a nonzero leftover here if the user
            // edits By Cash/By Bank without covering the full net total.
            // That leftover must not be silently saved as an implicit
            // credit — only a Credit bill (which has a customer on the
            // hook for it) is allowed to carry a By Credit balance.
            decimal.TryParse(txtByCredit.Text, out decimal byCredit);
            if (byCredit != 0 && cmbBillType.SelectedIndex != 1)
            {
                MessageBox.Show("By Cash + By Bank must add up to the Net Total for a Cash bill.\n\n" +
                    "Switch the Bill Type to Credit if this amount should go on the customer's account.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                txtByCash.Focus();
                return false;
            }

            return true;
        }

        private SalesSummary BuildSummaryFromUI()
        {
            decimal.TryParse(txtDiscountPercent.Text, out decimal discountPercent);
            decimal.TryParse(txtCashDiscount.Text, out decimal cashDiscount);
            decimal.TryParse(txtRoundOff.Text, out decimal roundOff);
            decimal.TryParse(lblGrossAmount.Text, out decimal grossAmount);
            decimal.TryParse(lblDiscountTotal.Text, out decimal discountTotal);
            decimal.TryParse(lblTaxTotal.Text, out decimal taxTotal);
            decimal.TryParse(lblNetTotal.Text, out decimal netTotal);

            decimal.TryParse(txtByCash.Text, out decimal byCash);
            decimal.TryParse(txtByBank.Text, out decimal byBank);
            decimal.TryParse(txtByCredit.Text, out decimal byCredit);

            int bankHeadCode = 0;
            string bankHeadName = "";
            if (cmbBankHead.SelectedItem is AcHeadLookup bank)
            {
                bankHeadCode = bank.AcHeadCode;
                bankHeadName = bank.AcHeadName;
            }

            string customerCode = null;
            string customerName;

            if (cmbBillType.SelectedIndex == 1 && cmbCustomer.SelectedItem is VendorCustomerMaster c)
            {
                customerCode = c.CustomerCode;
                customerName = c.CustomerName;
            }
            else
            {
                customerName = txtCashCustomerName.Text.Trim();
            }

            return new SalesSummary
            {
                // BuildSummaryFromUI is called for both Save and Update.
                // txtBillNoPreview.Text is blank in Save mode (nothing
                // claimed yet) and holds the real formatted bill number in
                // Update mode (set at load-for-edit — see the
                // txtBillNoPreview.Text = summary.FormattedBillNo line in
                // the load handler). Without this, summary.FormattedBillNo
                // was always null/empty here, and SalesRepository.Update
                // passes it straight through to PostToAccounts as the
                // voucher's VoucherRef — see SalesRepository.cs's own fix
                // for why that silently broke every edit, not just date
                // changes.
                FormattedBillNo = txtBillNoPreview.Text,
                BillDate = dpBillDate.SelectedDate ?? DateTime.Today,
                BillType = cmbBillType.SelectedIndex == 1 ? "Credit" : "Cash",
                CustomerCode = customerCode,
                CustomerName = customerName,
                CustomerAddress = txtCustomerAddress.Text.Trim(),
                CustomerPhone = txtCustomerPhone.Text.Trim(),
                CustomerGstNo = txtCustomerGstNo.Text.Trim(),
                State = txtState.Text.Trim(),
                StateCode = txtStateCode.Text.Trim(),

                GrossAmount = (double)grossAmount,
                DiscountAmount = (double)discountTotal,
                DiscountPercent = (double)discountPercent,
                TaxAmount = (double)taxTotal,
                CashDiscount = (double)cashDiscount,
                RoundOff = (double)roundOff,
                NetTotal = (double)netTotal,

                ToCashAmount = (double)byCash,
                ToBankAmount = (double)byBank,
                BankHeadCode = bankHeadCode,
                BankHeadName = bankHeadName,
                ToCreditAmount = (double)byCredit,

                TransporterName = txtTransporterName.Text.Trim(),
                VehicleNo = txtVehicleNo.Text.Trim(),
                PlaceOfDelivery = txtPlaceOfDelivery.Text.Trim(),
                DateOfDelivery = dpDateOfDelivery.SelectedDate?.ToString("yyyy-MM-dd"),
            };
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (!ValidateForm()) return;

            var summary = BuildSummaryFromUI();
            var lines = _itemRows.ToList();

            try
            {
                if (_saveMode == "Save")
                {
                    string prefix = (string)cmbPrefix.SelectedValue;
                    var (_, _, formatted) = _repo.Save(prefix, summary, lines, currentUser: "Admin");

                    MessageBox.Show($"Sales bill {formatted} saved successfully.", "Saved",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    _repo.Update(_editingPrefix, _editingBillNo, summary, lines, currentUser: "Admin");

                    MessageBox.Show("Sales bill updated successfully.", "Updated",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _saveMode = "Save";
                _editingPrefix = null;
                _editingBillNo = 0;
                //ClearAll();
                ToggleFields(false);
                btnPrint.IsEnabled = true;
                btnExport.IsEnabled = true;
                btnSave.Content = "_Save";
                btnCancelBill.IsEnabled = false;
                btnNew.Focus();

                // Refresh batch lookups — quantities just changed.
                ReloadBatchLookups();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving bill:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancelBill_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (_editingPrefix == null || _editingBillNo == 0) return;

            var result = MessageBox.Show(
                $"This will cancel bill {_editingPrefix}-{_editingBillNo} and reverse its stock effect.\n" +
                "The bill record is kept for audit — this cannot easily be undone.\n\nContinue?",
                "Confirm Cancel", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                _repo.CancelBill(_editingPrefix, _editingBillNo, currentUser: "Admin");
                MessageBox.Show("Bill cancelled.", "Cancelled",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                _saveMode = "Save";
                _editingPrefix = null;
                _editingBillNo = 0;
                ClearAll();
                ToggleFields(false);
                btnSave.Content = "_Save";
                btnCancelBill.IsEnabled = false;
                btnPrint.IsEnabled = false;
                btnExport.IsEnabled = false;
                btnNew.Focus();

                ReloadBatchLookups();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error cancelling bill:\n" + ex.Message,
                    "Cancel Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // PRINT / EXPORT — letterhead invoice, not the generic tabular report
        // helpers. Print always goes to a physical printer via
        // InvoicePrintHelper.Print; Export offers a choice between the exact
        // same letterhead as a PDF, or a plain tabular Excel workbook (see
        // InvoiceExcelHelper). Both read from the same InvoiceData built by
        // BuildInvoiceData() below, off whatever's currently on screen — so
        // Print/Export always matches what's actually loaded/displayed.
        // ════════════════════════════════════════════════════════════════════
        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_itemRows.Count == 0)
            {
                MessageBox.Show("There is nothing to print. Display a bill first.",
                    "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            InvoicePrintHelper.Print(BuildInvoiceData());
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            popupExport.IsOpen = !popupExport.IsOpen;
        }

        private void BtnExportPdf_Click(object sender, RoutedEventArgs e)
        {
            popupExport.IsOpen = false;

            if (_itemRows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Display a bill first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            InvoicePrintHelper.ExportPdf(BuildInvoiceData());
        }

        private void BtnExportExcel_Click(object sender, RoutedEventArgs e)
        {
            popupExport.IsOpen = false;

            if (_itemRows.Count == 0)
            {
                MessageBox.Show("There is nothing to export. Display a bill first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            InvoiceExcelHelper.Export(BuildInvoiceData());
        }

        /// <summary>
        /// Maps whatever's currently on screen (header fields + _itemRows)
        /// into the flat InvoiceData shape Print/PDF/Excel all render from.
        /// Deliberately reads the UI directly rather than re-fetching from
        /// the repository — this is what lets Print/Export work immediately
        /// after Display, without a redundant DB round-trip, and always
        /// reflects unsaved edits the same way Save would.
        /// </summary>
        private InvoiceData BuildInvoiceData()
        {
            var summary = BuildSummaryFromUI();

            // Interstate vs local (CGST+SGST split) comes from the series'
            // own Transaction Type, since that's what the bill was actually
            // raised under — not re-derived from state codes here.
            string prefix = _editingPrefix ?? (string)cmbPrefix.SelectedValue;
            var series = _allSeries.FirstOrDefault(s => s.Prefix == prefix);
            bool isInterstate = series != null &&
                (series.TransactionType == "Interstate" || series.TransactionType == "Export");

            var billTo = new InvoiceParty
            {
                Name = summary.CustomerName,
                AddressLines = string.IsNullOrWhiteSpace(summary.CustomerAddress)
                    ? new List<string>()
                    : summary.CustomerAddress.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
                State = summary.State,
                StateCode = summary.StateCode,
                Phone = summary.CustomerPhone,
                Mobile = summary.CustomerPhone,
                Gstin = summary.CustomerGstNo
            };

            // Letterhead — read fresh from company_details every time rather
            // than cached on the view, since Company Details can be edited
            // in a different tab while a bill is open.
            var company = _companyRepo.Get();

            var data = new InvoiceData
            {
                FormattedBillNo = string.IsNullOrWhiteSpace(txtBillNoPreview.Text)
                    ? (summary.FormattedBillNo ?? "") : txtBillNoPreview.Text,
                BillDate = (dpBillDate.SelectedDate ?? DateTime.Today).ToString("dd-MMM-yyyy"),
                TransportMode = summary.TransporterName,
                VehicleNo = summary.VehicleNo,
                BillTo = billTo,
                ShipTo = billTo,   // no separate ship-to captured yet — same as bill-to
                IsInterstate = isInterstate,
                RoundOff = summary.RoundOff,
                NetTotal = summary.NetTotal,

                CompanyName = company.CompanyName,
                CompanyRegNumber = company.RegNumber,
                CompanyAddressLines = string.IsNullOrWhiteSpace(company.Address)
                    ? new List<string>()
                    : company.Address
                        .Split('\n')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList(),
                CompanyPhone1Label = company.Phone1Label,
                CompanyPhone1 = company.Phone1,
                CompanyPhone2Label = company.Phone2Label,
                CompanyPhone2 = company.Phone2,
                CompanyEmail = company.Email,
                CompanyLogoPath = company.LogoPath,
                CompanyBank1Name = company.Bank1Name,
                CompanyBank1AccountNo = company.Bank1AccountNo,
                CompanyBank1AccountHolder = company.Bank1AccountHolder,
                CompanyBank2Name = company.Bank2Name,
                CompanyBank2AccountNo = company.Bank2AccountNo,
                CompanyBank2AccountHolder = company.Bank2AccountHolder
            };

            int slNo = 1;
            foreach (var row in _itemRows)
            {
                data.Lines.Add(new InvoiceLine
                {
                    SlNo = slNo++,
                    Particulars = row.ItemName,
                    HsnCode = row.HsnCode,
                    Rate = (double)row.UnitPrice,
                    Qty = (double)row.Quantity,
                    GrossAmt = (double)(row.UnitPrice * row.Quantity),
                    DiscAmt = (double)row.Discount,
                    TaxableValue = (double)row.TaxableAmount,
                    TaxPercent = (double)row.TaxRate,
                    TaxAmount = (double)row.TaxAmount,
                    Total = (double)row.Total
                });
            }

            return data;
        }

        // ════════════════════════════════════════════════════════════════════
        // SEARCH OVERLAY
        // ════════════════════════════════════════════════════════════════════
        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allBills = _repo.GetAll();
            var config = new SearchConfig
            {
                Title = "Search Sales Bills",
                SearchProperties = new List<string> { "FormattedBillNo", "CustomerName", "CustomerPhone" },

                ShowDateFilter = true,
                DateFilterProperty = "BillDate",

                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "Bill No",   BindingPath = "FormattedBillNo", Width = "130" },
                    new SearchColumn { Header = "Date",      BindingPath = "BillDate",         Width = "110", StringFormat = "{0:dd/MM/yyyy}" },
                    new SearchColumn { Header = "Customer",  BindingPath = "CustomerName",     Width = "*" },
                    new SearchColumn { Header = "Phone",     BindingPath = "CustomerPhone",    Width = "120" },
                    new SearchColumn { Header = "Net Total", BindingPath = "NetTotal",         Width = "100" },
                    new SearchColumn { Header = "Cancelled", BindingPath = "IsCancelled",      Width = "80" },
                },
                TargetOnSelect = btnSave
            };
            SearchOverlay.LoadData(allBills, config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is SalesSummary selected)
                LoadBillForEdit(selected.Prefix, selected.BillNo);
        }

        private void SearchOverlay_Closed(object sender, EventArgs e) => btnDisplay.Focus();

        /// <summary>
        /// Called by MainWindow when this view's tab is actually closed
        /// (LayoutDocument.Closed), NOT when the tab is merely switched away
        /// from. See EntryEditNavigator's remarks — unsubscribing on
        /// UserControl.Unloaded would silently kill "Edit This Bill" from a
        /// report the moment Sales Entry isn't the active AvalonDock tab.
        /// </summary>
        public void Dispose()
        {
            EntryEditNavigator.Unsubscribe<(string Prefix, int BillNo)>(EntryChannels.SalesEntry, OnEditRequested);
        }

        private void OnEditRequested((string Prefix, int BillNo) key)
        {
            // Already showing exactly this bill — nothing to switch.
            if (_saveMode == "Update" && _editingPrefix == key.Prefix && _editingBillNo == key.BillNo)
                return;

            if (HasUnsavedWork())
            {
                string currentLabel = _editingPrefix != null
                    ? $"Sales Bill {txtBillNoPreview.Text}"
                    : "the new sales bill you're entering";

                var result = MessageBox.Show(
                    $"You have unsaved changes to {currentLabel}.\n\n" +
                    $"Save it before switching to the selected bill?",
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

            LoadBillForEdit(key.Prefix, key.BillNo);
        }

        /// <summary>
        /// True if the form currently has entry work that hasn't been saved:
        /// either an existing bill loaded for edit, or a fresh bill with at
        /// least one item row or a bill number preview already generated.
        /// Only meaningful while the form is actually active
        /// (ToggleFields(true)) — an idle form with disabled fields has
        /// nothing to lose.
        /// </summary>
        private bool HasUnsavedWork()
        {
            if (!btnSave.IsEnabled) return false;

            return _editingPrefix != null
                || _itemRows.Any(r => !string.IsNullOrWhiteSpace(r.ItemCode))
                || !string.IsNullOrWhiteSpace(txtBillNoPreview.Text);
        }

        private void LoadBillForEdit(string prefix, int billNo)
        {
            _isLoadingBillForEdit = true;
            try
            {
                var summary = _repo.GetSummary(prefix, billNo);
                if (summary == null) return;

                var items = _repo.GetItems(prefix, billNo);

                _editingPrefix = prefix;
                _editingBillNo = billNo;
                _saveMode = "Update";
                _isCancelledBillLoaded = summary.IsCancelled == 1;

                ToggleFields(!_isCancelledBillLoaded);

                // Series is chosen once when a bill is created and must never
                // change on an existing bill (it's baked into the formatted
                // bill number already claimed from that series). Always
                // locked here, regardless of the enabled/cancelled state
                // ToggleFields just applied.
                cmbPrefix.IsEnabled = false;

                // _allSeries only holds ACTIVE series (see LoadLookups) —
                // an older bill can easily belong to a series that has since
                // been deactivated (e.g. last financial year's series). If
                // the bill's prefix isn't in that list, cmbPrefix has nothing
                // to bind SelectedValue against and silently shows blank.
                // Fall back to a synthetic entry just so the combo has
                // something to display; it's disabled anyway so it can't be
                // picked or mistaken for a live, selectable option.
                var matchedSeries = _allSeries.FirstOrDefault(s => s.Prefix == prefix);
                if (matchedSeries == null)
                {
                    cmbPrefix.ItemsSource = new List<SalesSeriesMaster>(_allSeries)
                    {
                        new SalesSeriesMaster { Prefix = prefix }
                    };
                }

                cmbPrefix.SelectedValue = prefix;
                lblSeriesCategory.Text = matchedSeries != null
                    ? $"{matchedSeries.CustomerType} / {matchedSeries.TransactionType}"
                    : "—";

                // Set explicitly rather than relying on CmbPrefix_SelectionChanged
                // (guarded off during load — see that handler) to (re)compute it.
                txtBillNoPreview.Text = summary.FormattedBillNo;

                dpBillDate.SelectedDate = summary.BillDate;
                cmbBillType.SelectedIndex = summary.BillType == "Credit" ? 1 : 0;
                ApplyCustomerMode();

                if (summary.BillType == "Credit")
                    cmbCustomer.SelectedValue = summary.CustomerCode;
                else
                    txtCashCustomerName.Text = summary.CustomerName;

                txtCustomerAddress.Text = summary.CustomerAddress;
                txtCustomerGstNo.Text = summary.CustomerGstNo;
                txtCustomerPhone.Text = summary.CustomerPhone;
                txtState.Text = summary.State;
                txtStateCode.Text = summary.StateCode;

                _itemRows.Clear();
                foreach (var it in items)
                {
                    _itemRows.Add(new SalesLine
                    {
                        ItemCode = it.ItemCode,
                        ItemName = it.ItemName,
                        BatchId = it.BatchId,
                        BatchNumber = it.BatchNumber,
                        TransId = it.TransId,
                        Mrp = (decimal)it.Mrp,
                        Rate = (decimal)it.Rate,
                        UnitPrice = it.UnitPrice > 0
                            ? (decimal)it.UnitPrice
                            : ComputeUnitPrice((decimal)it.Rate, (decimal)it.TaxPercent),
                        Quantity = (decimal)it.Qty,
                        DiscountRate = (decimal)it.DiscountPercent,
                        Discount = (decimal)it.DiscountAmount,
                        TaxableAmount = (decimal)it.TaxableAmount,
                        TaxRate = (decimal)it.TaxPercent,
                        TaxAmount = (decimal)it.TaxAmount,
                        Total = (decimal)it.TotalAmount
                    });
                }

                txtTransporterName.Text = summary.TransporterName;
                txtVehicleNo.Text = summary.VehicleNo;
                txtPlaceOfDelivery.Text = summary.PlaceOfDelivery;
                dpDateOfDelivery.SelectedDate = string.IsNullOrWhiteSpace(summary.DateOfDelivery)
                    ? (DateTime?)null : DateTime.Parse(summary.DateOfDelivery);

                txtDiscountPercent.Text = summary.DiscountPercent.ToString("0.00");
                txtCashDiscount.Text = summary.CashDiscount.ToString("0.00");
                txtRoundOff.Text = summary.RoundOff.ToString("0.00");

                // Loaded verbatim from the saved bill — with _isLoadingBillForEdit
                // still true, none of these Text assignments (nor the item adds
                // above) trigger a settlement recalculation that would
                // overwrite them.
                txtByCash.Text = summary.ToCashAmount.ToString("0.00");
                txtByBank.Text = summary.ToBankAmount.ToString("0.00");
                // cmbBankHead's SelectedValuePath is "AcHeadCode", a string
                // property on AcHeadLookup — SelectedValue must be set as a
                // string too, or WPF's value match silently fails (int never
                // equals string) and the combo comes up blank even though
                // BankHeadCode saved correctly.
                cmbBankHead.SelectedValue = summary.BankHeadCode > 0 ? summary.BankHeadCode.ToString() : null;
                txtByCredit.Text = summary.ToCreditAmount.ToString("0.00");

                // The bill already has its own saved cash/bank/credit split —
                // don't let RecalcTotals() below auto-overwrite By Bank with
                // the net total once loading finishes and normal edits resume.
                _toBankManuallyEdited = true;

                // Refreshes the Gross/Discount/Tax/Net Total labels only —
                // still guarded by _isLoadingBillForEdit, so By Cash/By Bank/
                // By Credit are left exactly as just loaded.
                RecalcTotals();

                if (_isCancelledBillLoaded)
                {
                    pnlCancelledWatermark.Visibility = Visibility.Visible;
                    btnSave.IsEnabled = false;
                    btnCancelBill.IsEnabled = false;

                    btnPrint.IsEnabled = false;
                    btnExport.IsEnabled = false;
                }
                else if (!BillPro.Helpers.PermissionGate.CanEdit)
                {
                    // Viewer without edit rights: treat a freshly-displayed bill exactly
                    // like one that was just saved — locked fields, Print/Export
                    // available. Delete/Cancel is left as-is (still gated at click time
                    // by PermissionGate.RequireDelete in BtnCancelBill_Click), since
                    // that check already covers it correctly.
                    pnlCancelledWatermark.Visibility = Visibility.Collapsed;
                    ToggleFields(false);
                    btnSave.Content = "_Update";
                    btnCancelBill.IsEnabled = true;

                    btnPrint.IsEnabled = true;
                    btnExport.IsEnabled = true;
                }
                else
                {
                    pnlCancelledWatermark.Visibility = Visibility.Collapsed;
                    btnSave.Content = "_Update";
                    btnCancelBill.IsEnabled = true;

                    btnPrint.IsEnabled = false;
                    btnExport.IsEnabled = false;
                }
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
    }
}