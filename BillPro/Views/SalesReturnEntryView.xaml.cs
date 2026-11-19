using Syncfusion.UI.Xaml.Grid.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace BillPro.Views
{
    public partial class SalesReturnEntryView : UserControl, IDisposable
    {
        private readonly SalesReturnRepository _repo = new();
        private readonly VendorCustomerRepository _customerRepo = new();
        private readonly CompanyRepository _companyRepo = new();

        public ObservableCollection<SalesReturnItemLookup> AllBatchLookups { get; } = new();
        public ObservableCollection<VendorCustomerMaster> AllCustomersList { get; } = new();
        public ObservableCollection<AcHeadLookup> AllBankHeadsList { get; } = new();

        private ObservableCollection<SalesReturnLine> _itemRows = new();

        private string _saveMode = "Save";
        private int _editingEntryNo = 0;
        private bool _isCancelledLoaded = false;
        private bool _isLoadingForEdit = false;

        private bool _toBankManuallyEdited = false;
        private bool _isRecalculatingSettlement = false;

        // Guards EntryCalculation_TextChanged's cascade against re-entrancy
        // (setting one box's .Text from inside the handler would otherwise
        // re-fire TextChanged on that box too) — mirrors SalesEntryView's
        // _isCalculatingEntry.
        private bool _isCalculatingEntry = false;

        // Quantity captured just before an in-grid cell edit begins, so
        // SfItems_CurrentCellEndEdit can adjust the batch's live stock by
        // only the delta if the operator changes Quantity directly in the
        // grid, and can roll the cell back if the committed value is
        // invalid (<= 0) — mirrors SalesEntryView's _qtyBeforeGridEdit /
        // PurchaseEntryView's SfItems_CurrentCellEndEdit.
        private decimal _qtyBeforeGridEdit = 0;

        // Bank account the operator last had selected — carried forward
        // into the next New entry instead of clearing to blank each time.
        private string _lastSelectedBankHeadCode = null;

        // Full discount total (line + bill% + cash), used for persistence —
        // kept separate from the Discount Total label, which deliberately
        // shows only the bill-level portion.
        private decimal _lastTotalDiscount = 0;

        public SalesReturnEntryView()
        {
            InitializeComponent();
            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;
            DataContext = this;

            _repo.InitializeDatabase();

            sfItems.ItemsSource = _itemRows;
            _itemRows.CollectionChanged += (_, __) => RecalcTotals();

            cmbEntryItem.ItemsSource = AllBatchLookups;
            cmbCustomer.ItemsSource = AllCustomersList;
            cmbBankHead.ItemsSource = AllBankHeadsList;
            EntryEditNavigator.Subscribe<int>(EntryChannels.SalesReturnEntry, OnEditRequested);

            LoadLookups();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => btnNew.Focus()));
        }

        // ════════════════════════════════════════════════════════════════
        // DATA LOADING
        // ════════════════════════════════════════════════════════════════
        private void LoadLookups()
        {
            try
            {
                AllBatchLookups.Clear();
                foreach (var b in _repo.GetReturnBatchLookups())
                    AllBatchLookups.Add(b);

                AllCustomersList.Clear();
                foreach (var c in _customerRepo.GetAll())
                    AllCustomersList.Add(c);

                // Sales returns can be refunded via bank same as Sales Entry —
                // now backed by the same shared BillPro.DataAccess.BankAccountLookup
                // query as every other entry screen, via this view's own
                // SalesReturnRepository instead of reaching into SalesRepository.
                AllBankHeadsList.Clear();
                foreach (var h in _repo.GetBankAcHeads())
                    AllBankHeadsList.Add(h);

                // Restore the last-used bank account if it's still in the
                // list, otherwise default to the first one — the combo
                // loses its selection whenever this list is rebuilt.
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

        // ════════════════════════════════════════════════════════════════
        // UI STATE
        // ════════════════════════════════════════════════════════════════
        private void ToggleFields(bool enabled)
        {
            dpEntryDate.IsEnabled = enabled;
            txtOriginalBillNo.IsEnabled = enabled;
            cmbBillType.IsEnabled = enabled;

            ApplyCustomerMode();
            if (!enabled)
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
            cmbEntryItem.IsEnabled = enabled;
            txtEntryRate.IsEnabled = enabled;
            txtEntryQty.IsEnabled = enabled;
            txtEntryDiscount.IsEnabled = enabled;
            txtEntryDiscountAmount.IsEnabled = enabled;
            txtEntryTaxable.IsEnabled = enabled;
            txtEntryTaxRate.IsEnabled = enabled;
            txtEntryTaxAmount.IsEnabled = enabled;
            btnAddItem.IsEnabled = enabled;

            txtDiscountPercent.IsEnabled = enabled;
            txtCashDiscount.IsEnabled = enabled;
            txtRoundOff.IsEnabled = enabled;

            txtByCash.IsEnabled = enabled;
            txtByBank.IsEnabled = enabled;
            cmbBankHead.IsEnabled = enabled;

            btnSave.IsEnabled = enabled;
            pnlCancelledWatermark.Visibility = Visibility.Collapsed;
        }

        private void ApplyCustomerMode()
        {
            bool isCredit = (cmbBillType.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Credit";
            txtCashCustomerName.Visibility = isCredit ? Visibility.Collapsed : Visibility.Visible;
            cmbCustomer.Visibility = isCredit ? Visibility.Visible : Visibility.Collapsed;
            txtCashCustomerName.IsEnabled = !isCredit;
            cmbCustomer.IsEnabled = isCredit;
        }

        private void CmbBillType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyCustomerMode();
        }

        private void ClearAll()
        {
            _editingEntryNo = 0;
            _saveMode = "Save";
            _isCancelledLoaded = false;

            txtEntryNoPreview.Text = _repo.FormatEntryNo(_repo.GetNextEntryNoPreview());
            dpEntryDate.SelectedDate = DateTime.Today;
            txtOriginalBillNo.Text = "";
            lblOriginalBillWarning.Visibility = Visibility.Collapsed;
            cmbBillType.SelectedIndex = 0;

            txtCashCustomerName.Text = "";
            cmbCustomer.SelectedIndex = -1;
            txtCustomerAddress.Text = "";
            txtCustomerGstNo.Text = "";
            txtCustomerPhone.Text = "";
            txtState.Text = "";
            txtStateCode.Text = "";

            _itemRows.Clear();
            ClearItemEntryLine();

            txtDiscountPercent.Text = "0";
            txtCashDiscount.Text = "0";
            txtRoundOff.Text = "0";
            txtByCash.Text = "0";
            txtByBank.Text = "0";
            // Capture whatever bank account was in use before wiping the
            // form, so the next New entry starts on the same one instead
            // of blank — LoadLookups() re-applies it once AllBankHeadsList
            // is rebuilt.
            if (cmbBankHead.SelectedValue != null)
                _lastSelectedBankHeadCode = cmbBankHead.SelectedValue.ToString();
            txtByCredit.Text = "0";
            _toBankManuallyEdited = false;

            ToggleFields(true);
            btnSave.Content = "_Save";
            btnCancelBill.IsEnabled = false;
            btnPrint.IsEnabled = false;
            btnExport.IsEnabled = false;

            RecalcTotals();
        }

        private void ClearItemEntryLine()
        {
            cmbEntryItem.SelectionChanged -= CmbEntryItem_SelectionChanged;
            cmbEntryItem.SelectedIndex = -1;
            cmbEntryItem.Text = "";
            cmbEntryItem.SelectionChanged += CmbEntryItem_SelectionChanged;

            txtEntryHsn.Text = "";
            txtEntryRate.Text = "";
            txtEntryQty.Text = "";
            txtEntryDiscount.Text = "0.00";
            txtEntryDiscountAmount.Text = "0.00";
            txtEntryTaxable.Text = "";
            txtEntryTaxRate.Text = "";
            txtEntryTaxAmount.Text = "0.00";
            txtEntryTotal.Text = "";
            lblAvailableStock.Text = "";
        }

        // ════════════════════════════════════════════════════════════════
        // ORIGINAL BILL VALIDATION (non-blocking)
        // ════════════════════════════════════════════════════════════════
        private void TxtOriginalBillNo_LostFocus(object sender, RoutedEventArgs e)
        {
            string billNo = txtOriginalBillNo.Text?.Trim();
            if (string.IsNullOrEmpty(billNo))
            {
                lblOriginalBillWarning.Visibility = Visibility.Collapsed;
                return;
            }

            bool exists = _repo.OriginalBillExists(billNo);
            lblOriginalBillWarning.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        }

        // ════════════════════════════════════════════════════════════════
        // CUSTOMER LOOKUP
        // ════════════════════════════════════════════════════════════════
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
            Dispatcher.BeginInvoke(new Action(() => cmbCashCustomerLookup.Focus()), DispatcherPriority.Input);
        }

        private void CmbCashCustomerLookup_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not VendorCustomerMaster c) return;
            txtCashCustomerName.Text = c.CustomerName;
            FillCustomerDetails(c);
            popupCustomerLookup.IsOpen = false;
            //txtCustomerAddress.Focus();
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

        // ════════════════════════════════════════════════════════════════
        // ITEM ENTRY LINE
        // ════════════════════════════════════════════════════════════════
        private void CmbEntryItem_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not SalesReturnItemLookup selected) return;

            txtEntryHsn.Text = selected.HsnCode;
            txtEntryRate.Text = ((decimal)selected.SalesRate).ToString("N2");
            txtEntryDiscount.Text = "0.00";
            txtEntryTaxRate.Text = selected.TaxRate.ToString("N2");
            txtEntryQty.Text = "";
            lblAvailableStock.Text = $"Current stock: {selected.CurrentStock:N3}";

            Dispatcher.BeginInvoke(new Action(() => txtEntryQty.Focus()), DispatcherPriority.Input);
        }

        // Rate is tax-inclusive (what the customer was refunded per unit);
        // tax is backed out of it rather than added on top. Deliberately
        // unrounded — see SalesEntryView.ComputeUnitPrice for the full
        // rationale (rounding here before multiplying by Quantity is what
        // produces mismatched totals on larger quantities).
        private static decimal ComputeUnitPrice(decimal taxInclusiveRate, decimal taxRate) =>
            taxInclusiveRate * 100m / (100m + taxRate);

        private void EntryLine_RecalcRequested(object sender, TextChangedEventArgs e) => EntryCalculation_TextChanged(sender, e);

        /// <summary>
        /// Rate / Qty / Discount / Tax cascade for the entry line — mirrors
        /// SalesEntryView's EntryCalculation_TextChanged exactly: editing
        /// Rate/Qty/Disc% recomputes everything downstream; editing Disc Amt,
        /// Taxable, or Tax % recomputes only what's downstream of THAT field,
        /// leaving the operator free to override Disc Amt/Taxable/Tax Amt by
        /// hand (e.g. to match a vendor's/customer's own rounding) without
        /// the box immediately snapping back to the computed value.
        /// </summary>
        private void EntryCalculation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isCalculatingEntry) return;
            if (sender is not TextBox tb) return;

            decimal.TryParse(txtEntryRate.Text, out decimal rate);
            decimal.TryParse(txtEntryQty.Text, out decimal qty);
            decimal.TryParse(txtEntryDiscount.Text, out decimal discPercent);
            decimal.TryParse(txtEntryDiscountAmount.Text, out decimal discAmt);
            decimal.TryParse(txtEntryTaxable.Text, out decimal taxable);
            decimal.TryParse(txtEntryTaxRate.Text, out decimal taxRate);
            decimal.TryParse(txtEntryTaxAmount.Text, out decimal taxAmt);

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

        private void BtnAddItem_Click(object sender, RoutedEventArgs e) => CommitItemEntry();

        private void CommitItemEntry()
        {
            if (cmbEntryItem.SelectedItem is not SalesReturnItemLookup selected)
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

            decimal.TryParse(txtEntryRate.Text, out decimal rate);
            decimal.TryParse(txtEntryDiscount.Text, out decimal discRate);
            decimal.TryParse(txtEntryDiscountAmount.Text, out decimal discAmt);
            decimal.TryParse(txtEntryTaxable.Text, out decimal taxable);
            decimal.TryParse(txtEntryTaxRate.Text, out decimal taxRate);
            decimal.TryParse(txtEntryTaxAmount.Text, out decimal taxAmt);
            decimal.TryParse(txtEntryTotal.Text, out decimal total);

            _itemRows.Add(new SalesReturnLine
            {
                ItemCode = selected.ItemCode,
                ItemName = selected.ItemName,
                BatchId = selected.BatchId,
                BatchNumber = selected.BatchNumber,
                Rate = rate,
                UnitPrice = ComputeUnitPrice(rate, taxRate),
                Quantity = qty,
                DiscountRate = discRate,
                Discount = discAmt,
                TaxableAmount = taxable,
                TaxRate = taxRate,
                TaxAmount = taxAmt,
                Total = total
            });

            // Reflect the return in-session: this batch's live stock in the
            // lookup goes UP (opposite of Sales Entry, which decrements it).
            selected.CurrentStock += (double)qty;

            ClearItemEntryLine();
            Dispatcher.BeginInvoke(new Action(() => cmbEntryItem.Focus()), DispatcherPriority.Input);
        }

        private void BtnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not SalesReturnLine row) return;

            // Undo the in-session stock bump this row applied.
            var batch = AllBatchLookups.FirstOrDefault(b => b.BatchId == row.BatchId);
            if (batch != null) batch.CurrentStock -= (double)row.Quantity;

            _itemRows.Remove(row);
        }

        private void SfItems_CurrentCellBeginEdit(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellBeginEditEventArgs e)
        {
            if (sender is not Syncfusion.UI.Xaml.Grid.SfDataGrid grid) return;
            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is SalesReturnLine row)
                _qtyBeforeGridEdit = row.Quantity;
        }

        private void SfItems_CurrentCellEndEdit(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellEndEditEventArgs e)
        {
            if (sender is not Syncfusion.UI.Xaml.Grid.SfDataGrid grid) return;
            if (grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) is not SalesReturnLine row) return;

            // Reject a zero/negative quantity committed directly in the grid.
            // The rollback is deferred via Dispatcher.BeginInvoke: at this
            // point the grid is still mid-way through committing/refreshing
            // the cell that was just edited, so setting row.Quantity here
            // synchronously gets silently overwritten a moment later by the
            // grid's own post-edit refresh — the cell would visually stay at
            // 0 even though the bound value was reset. Posting the rollback
            // to run after the grid's current edit cycle finishes ensures it
            // actually sticks, and Refresh() + RecalcTotals() then repaint
            // the corrected value and totals. Mirrors PurchaseEntryView's
            // SfItems_CurrentCellEndEdit.
            if (row.Quantity <= 0)
            {
                MessageBox.Show("Quantity must be greater than 0.", "Invalid Quantity",
                    MessageBoxButton.OK, MessageBoxImage.Warning);

                decimal restoreQty = _qtyBeforeGridEdit;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    row.Quantity = restoreQty;
                    grid.View.Refresh();
                    RecalcTotals();
                }), DispatcherPriority.Background);

                return;
            }

            // Keep the derived fields consistent when Rate/Qty/Disc%/Disc
            // Amt/Tax% are edited directly in the grid — previously nothing
            // recomputed these, so a direct grid edit left Taxable/Tax/Total
            // (and therefore the bill totals) stale until the row was
            // re-added. Mirrors SalesEntryView's SfItems_CurrentCellEndEdit,
            // except Disc Amt itself is now editable here too: editing it
            // directly is respected as-is instead of being silently
            // recomputed back from Disc% right after.
            row.UnitPrice = ComputeUnitPrice(row.Rate, row.TaxRate);
            decimal gross = row.UnitPrice * row.Quantity;
            string editedColumn = (e.RowColumnIndex.ColumnIndex >= 0 && e.RowColumnIndex.ColumnIndex < grid.Columns.Count)
                ? grid.Columns[e.RowColumnIndex.ColumnIndex].MappingName
                : null;

            if (editedColumn == "Discount")
            {
                // Operator typed a Disc Amt directly — keep it, don't
                // recompute it from DiscountRate.
            }
            else
            {
                row.Discount = Math.Round(gross * (row.DiscountRate / 100m), 2);
            }

            row.TaxableAmount = gross - row.Discount;
            row.TaxAmount = Math.Round(row.TaxableAmount * (row.TaxRate / 100m), 2);
            row.Total = row.TaxableAmount + row.TaxAmount;

            // If Quantity itself was the field edited, apply only the delta
            // to the batch's live stock. Returns increase stock (opposite of
            // Sales Entry), so a bigger quantity means MORE stock, not less.
            if (row.Quantity != _qtyBeforeGridEdit && row.BatchId != 0)
            {
                var lookup = AllBatchLookups.FirstOrDefault(b => b.BatchId == row.BatchId);
                if (lookup != null)
                {
                    decimal delta = row.Quantity - _qtyBeforeGridEdit; // +ve = more returned, -ve = less
                    lookup.CurrentStock += (double)delta;
                }
            }

            RecalcTotals();
        }

        // NOTE: A RowValidating handler previously lived here
        // (SfItems_RowValidating), rejecting Quantity <= 0 at the grid
        // level. It has been REMOVED — Syncfusion's SfDataGrid.RowValidating
        // pipeline walks every DataColumnBase (including unbound/template
        // columns such as the Delete-button column, which has a null
        // MappingName) and does e.ErrorMessages.ContainsKey(column.MappingName)
        // for each one. A null MappingName throws ArgumentNullException deep
        // inside Syncfusion's ValidationHelper, crashing the app on row
        // commit (e.g. pressing Enter/Tab out of a grid row) regardless of
        // whether this handler's own logic ever ran.
        //
        // No validation coverage is lost by removing it: Quantity <= 0 is
        // now rejected directly in SfItems_CurrentCellEndEdit above (with a
        // proper rollback to the pre-edit value), and CommitItemEntry()
        // already blocks adding a new row with qty <= 0 from the entry line.

        // ════════════════════════════════════════════════════════════════
        // TOTALS + SETTLEMENT
        // ════════════════════════════════════════════════════════════════
        private void Totals_RecalcRequested(object sender, TextChangedEventArgs e) => RecalcTotals();

        private void RecalcTotals()
        {
            decimal gross = _itemRows.Sum(r => r.UnitPrice * r.Quantity);
            decimal lineDiscount = _itemRows.Sum(r => r.Discount);
            decimal taxAmount = _itemRows.Sum(r => r.TaxAmount);

            decimal.TryParse(txtDiscountPercent.Text, out decimal billDiscPercent);
            decimal.TryParse(txtCashDiscount.Text, out decimal cashDiscount);
            decimal.TryParse(txtRoundOff.Text, out decimal roundOff);

            decimal billDiscount = Math.Round((gross - lineDiscount) * billDiscPercent / 100m, 2);
            // Cash Discount folds straight into the same combined Discount
            // Amount total as billDiscount (there's no separate persisted
            // column for it on SalesReturnSummary, same as how Purchase/
            // Sales Entry's own DiscountAmount is "line + bill discount
            // combined") — it still nets out of Net Total correctly below.
            decimal totalDiscount = lineDiscount + billDiscount + cashDiscount;
            decimal netTotal = gross - totalDiscount + taxAmount + roundOff;
            _lastTotalDiscount = totalDiscount;

            // The Discount Total label deliberately excludes per-row (grid)
            // discounts — it only reflects the bill-level Discount % and
            // Cash Discount entered in the footer. Line discounts are still
            // netted out of Net Total above; they're just not shown here.
            decimal displayedDiscount = billDiscount + cashDiscount;

            lblGrossAmount.Text = gross.ToString("N2");
            lblDiscountAmount.Text = displayedDiscount.ToString("N2");
            lblTaxAmount.Text = taxAmount.ToString("N2");
            lblNetTotal.Text = netTotal.ToString("N2");

            if (_isLoadingForEdit) return;

            // Default "By Bank" to the full net total, same as Sales Entry —
            // the common case is the whole refund going out via bank/card.
            // Once the operator edits it themselves, stop overwriting it.
            if (!_toBankManuallyEdited)
                txtByBank.Text = netTotal.ToString("N2");

            RecalcSettlement(netTotal);
        }

        private void TxtByCash_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isRecalculatingSettlement || _isLoadingForEdit) return;

            // Editing By Cash directly also pins the split (same as editing
            // By Bank) — otherwise adding another item later would silently
            // reset By Bank back to the full net total and blow away the
            // split the operator just set up.
            if (txtByCash.IsKeyboardFocused)
                _toBankManuallyEdited = true;

            RecalcSettlement(recalcFromBank: false);
        }

        private void TxtByBank_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isRecalculatingSettlement || _isLoadingForEdit) return;

            if (txtByBank.IsKeyboardFocused)
                _toBankManuallyEdited = true;

            RecalcSettlement(recalcFromBank: true);
        }

        // ════════════════════════════════════════════════════════════════
        // SETTLEMENT CASCADE (By Cash / By Bank / By Credit) — mirrors
        // SalesEntryView.RecalcSettlement exactly:
        //   Editing By Cash : By Bank = Net Total − By Cash
        //   Editing By Bank : By Cash remains untouched
        //   By Credit (always) = Net Total − By Bank − By Cash
        // ════════════════════════════════════════════════════════════════
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
                    txtByBank.Text = byBank.ToString("N2");
                }

                decimal.TryParse(txtByBank.Text, out decimal byBankValue);
                decimal.TryParse(txtByCash.Text, out decimal byCashValue);

                decimal byCredit = netTotal - byBankValue - byCashValue;
                txtByCredit.Text = byCredit.ToString("N2");
            }
            finally
            {
                _isRecalculatingSettlement = false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // NEW / SAVE / CANCEL
        // ════════════════════════════════════════════════════════════════
        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (HasUnsavedWork())
            {
                var confirm = MessageBox.Show("Discard unsaved changes and start a new entry?",
                    "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            ClearAll();

            // Starting a fresh entry discards any unsaved lines from the one
            // just left — re-pull stock/customer/bank lookups from the DB so
            // stale data doesn't linger into the new entry.
            LoadLookups();

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => dpEntryDate.Focus()));
        }

        private bool HasUnsavedWork()
        {
            if (!btnSave.IsEnabled) return false;
            return _editingEntryNo != 0 || _itemRows.Any(r => !string.IsNullOrWhiteSpace(r.ItemCode));
        }

        private bool ValidateForm(out string error)
        {
            error = null;

            if (dpEntryDate.SelectedDate == null)
            {
                error = "Please select an entry date.";
                dpEntryDate.Focus();
                return false;
            }

            bool isCredit = (cmbBillType.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Credit";
            if (isCredit && cmbCustomer.SelectedValue == null)
            {
                error = "Please select a customer for a credit return.";
                cmbCustomer.Focus();
                return false;
            }
            if (!isCredit && string.IsNullOrWhiteSpace(txtCashCustomerName.Text))
            {
                error = "Please enter a customer name.";
                txtCashCustomerName.Focus();
                return false;
            }

            if (_itemRows.Count == 0) { error = "Please add at least one item."; return false; }

            decimal.TryParse(txtByBank.Text, out decimal byBank);
            if (byBank != 0 && cmbBankHead.SelectedItem == null)
            {
                error = "A bank account must be selected when there is a bank/card settlement amount.";
                cmbBankHead.Focus();
                return false;
            }

            decimal.TryParse(txtByCredit.Text, out decimal byCredit);
            if (!isCredit && byCredit != 0)
            {
                error = "There is a credit (By Credit) amount pending, so this return must be set to Credit mode. Please select Credit or adjust the settlement amounts so By Credit is 0.";
                cmbBillType.Focus();
                return false;
            }

            return true;
        }

        private SalesReturnSummary BuildSummaryFromUI()
        {
            bool isCredit = (cmbBillType.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Credit";

            int bankHeadCode = 0;
            if (cmbBankHead.SelectedValue != null)
            {
                int.TryParse(cmbBankHead.SelectedValue.ToString(), out bankHeadCode);
            }

            return new SalesReturnSummary
            {
                EntryNo = _editingEntryNo,
                EntryDate = dpEntryDate.SelectedDate ?? DateTime.Today,
                OriginalBillNo = txtOriginalBillNo.Text?.Trim(),
                BillType = isCredit ? "Credit" : "Cash",
                CustomerCode = isCredit ? cmbCustomer.SelectedValue as string : null,
                CustomerName = isCredit ? (cmbCustomer.SelectedItem as VendorCustomerMaster)?.CustomerName : txtCashCustomerName.Text,
                CustomerAddress = txtCustomerAddress.Text,
                CustomerPhone = txtCustomerPhone.Text,
                CustomerGstNo = txtCustomerGstNo.Text,
                State = txtState.Text,
                StateCode = txtStateCode.Text,
                GrossAmount = double.TryParse(lblGrossAmount.Text, out var ga) ? ga : 0,
                DiscountAmount = (double)_lastTotalDiscount,
                DiscountPercent = double.TryParse(txtDiscountPercent.Text, out var dp) ? dp : 0,
                TaxAmount = double.TryParse(lblTaxAmount.Text, out var ta) ? ta : 0,
                RoundOff = double.TryParse(txtRoundOff.Text, out var ro) ? ro : 0,
                NetTotal = double.TryParse(lblNetTotal.Text, out var nt) ? nt : 0,
                ToCashAmount = double.TryParse(txtByCash.Text, out var bc) ? bc : 0,
                ToBankAmount = double.TryParse(txtByBank.Text, out var bb) ? bb : 0,
                BankHeadCode = bankHeadCode,
                BankHeadName = (cmbBankHead.SelectedItem as AcHeadLookup)?.AcHeadName,
                ToCreditAmount = double.TryParse(txtByCredit.Text, out var tc) ? tc : 0,
                CreatedBy = Environment.UserName,
                LastModifiedBy = Environment.UserName
            };
        }

        // ════════════════════════════════════════════════════════════════
        // SEARCH OVERLAY
        // ════════════════════════════════════════════════════════════════
        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allReturns = _repo.GetAll();

            var config = new SearchConfig
            {
                Title = "Search Sales Returns",
                SearchProperties = new List<string> { "FormattedEntryNo", "CustomerName", "CustomerPhone" },

                ShowDateFilter = true,
                DateFilterProperty = "EntryDate",

                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "Entry No",  BindingPath = "FormattedEntryNo", Width = "130" },
                    new SearchColumn { Header = "Date",      BindingPath = "EntryDate",        Width = "110", StringFormat = "{0:dd/MM/yyyy}" },
                    new SearchColumn { Header = "Customer",  BindingPath = "CustomerName",     Width = "*" },
                    new SearchColumn { Header = "Phone",     BindingPath = "CustomerPhone",    Width = "120" },
                    new SearchColumn { Header = "Net Total", BindingPath = "NetTotal",         Width = "100" },
                    new SearchColumn { Header = "Cancelled", BindingPath = "IsCancelled",      Width = "80" },
                },
                TargetOnSelect = btnSave
            };

            SearchOverlay.LoadData(allReturns, config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is SalesReturnSummary selected)
                LoadEntryForEdit(selected.EntryNo);
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            btnDisplay.Focus();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (!ValidateForm(out string error))
            {
                MessageBox.Show(error, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var summary = BuildSummaryFromUI();

                var items = _itemRows.Select(r => new SalesReturnItemDetail
                {
                    ItemCode = r.ItemCode,
                    ItemName = r.ItemName,
                    BatchId = r.BatchId,
                    BatchNumber = r.BatchNumber,
                    Qty = (double)r.Quantity,
                    Rate = (double)r.Rate,
                    DiscountPercent = (double)r.DiscountRate,
                    DiscountAmount = (double)r.Discount,
                    TaxableAmount = (double)r.TaxableAmount,
                    TaxPercent = (double)r.TaxRate,
                    TaxAmount = (double)r.TaxAmount,
                    TotalAmount = (double)r.Total
                }).ToList();

                if (_saveMode == "Save")
                {
                    var (entryNo, formatted) = _repo.Save(summary, items);
                    _editingEntryNo = entryNo;
                    txtEntryNoPreview.Text = formatted;
                }
                else
                {
                    _repo.Update(summary, items);
                }

                _saveMode = "Save";
                _editingEntryNo = 0;
                ToggleFields(false);
                btnPrint.IsEnabled = true;
                btnExport.IsEnabled = true;
                btnSave.Content = "_Save";
                btnCancelBill.IsEnabled = false;
                btnNew.Focus();

                // Refresh batch lookups — quantities just changed.
                LoadLookups();

                MessageBox.Show("Sales return saved successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving return:\n" + ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancelBill_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (_editingEntryNo == 0)
            {
                MessageBox.Show("Nothing to cancel — this entry hasn't been saved yet.",
                    "Cancel Entry", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show("Cancel this return entry? This reverses its stock effect.",
                "Confirm Cancel", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                _repo.CancelEntry(_editingEntryNo, Environment.UserName);
                LoadEntryForEdit(_editingEntryNo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error cancelling entry:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // PRINT / EXPORT
        // ════════════════════════════════════════════════════════════════════
        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_itemRows.Count == 0)
            {
                MessageBox.Show("There is nothing to print. Display a return first.",
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
                MessageBox.Show("There is nothing to export. Display a return first.",
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
                MessageBox.Show("There is nothing to export. Display a return first.",
                    "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            InvoiceExcelHelper.Export(BuildInvoiceData());
        }

        /// <summary>
        /// Maps the UI state directly to the InvoiceData format expected by the 
        /// PDF and Excel generation helpers.
        /// </summary>
        private InvoiceData BuildInvoiceData()
        {
            var summary = BuildSummaryFromUI();
            var company = _companyRepo.Get();

            // Adjust if Sales Returns explicitly track interstate/export status
            bool isInterstate = false;

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

            var data = new InvoiceData
            {
                FormattedBillNo = string.IsNullOrWhiteSpace(txtEntryNoPreview.Text)
                    ? (summary.EntryNo.ToString() ?? "") : txtEntryNoPreview.Text,
                BillDate = (dpEntryDate.SelectedDate ?? DateTime.Today).ToString("dd-MMM-yyyy"),

                // Return-specific labels
                InvoiceTitle = "CREDIT NOTE (SALES RETURN)",

                TransportMode = "",
                VehicleNo = "",

                BillTo = billTo,
                ShipTo = billTo,
                IsInterstate = isInterstate,
                RoundOff = summary.RoundOff,
                NetTotal = summary.NetTotal,

                CompanyName = company.CompanyName,
                CompanyRegNumber = company.RegNumber,
                CompanyAddressLines = string.IsNullOrWhiteSpace(company.Address)
                    ? new List<string>()
                    : company.Address.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
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
                    //HsnCode = row.HsnCode,
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

        // ════════════════════════════════════════════════════════════════
        // LOAD FOR EDIT
        // ════════════════════════════════════════════════════════════════
        private void LoadEntryForEdit(int entryNo)
        {
            _isLoadingForEdit = true;
            try
            {
                var summary = _repo.GetSummary(entryNo);
                if (summary == null) return;

                var items = _repo.GetItems(entryNo);

                _editingEntryNo = entryNo;
                _saveMode = "Update";
                _isCancelledLoaded = summary.IsCancelled == 1;

                bool canEdit = BillPro.Helpers.PermissionGate.CanEdit;
                ToggleFields(!_isCancelledLoaded && canEdit);

                txtEntryNoPreview.Text = summary.FormattedEntryNo;
                dpEntryDate.SelectedDate = summary.EntryDate;
                txtOriginalBillNo.Text = summary.OriginalBillNo;
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
                    _itemRows.Add(new SalesReturnLine
                    {
                        ItemCode = it.ItemCode,
                        ItemName = it.ItemName,
                        BatchId = it.BatchId,
                        BatchNumber = it.BatchNumber,
                        TransId = it.TransId,
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

                txtDiscountPercent.Text = summary.DiscountPercent.ToString("0.00");
                // Not separately persisted on SalesReturnSummary (folds into
                // the combined DiscountAmount, same as Discount %) — starts
                // back at 0 on reload; the saved DiscountAmount total still
                // reflects whatever Cash Discount was applied at Save time.
                txtCashDiscount.Text = "0.00";
                txtRoundOff.Text = summary.RoundOff.ToString("0.00");

                txtByCash.Text = summary.ToCashAmount.ToString("0.00");
                txtByBank.Text = summary.ToBankAmount.ToString("0.00");
                // cmbBankHead's SelectedValuePath is "AcHeadCode" (a string
                // on AcHeadLookup) — SelectedValue must match as a string too,
                // or WPF's value match silently fails and the bank combo
                // comes up blank even though BankHeadCode saved correctly.
                cmbBankHead.SelectedValue = summary.BankHeadCode > 0 ? summary.BankHeadCode.ToString() : null;
                txtByCredit.Text = summary.ToCreditAmount.ToString("0.00");
                _toBankManuallyEdited = true;

                RecalcTotals();

                if (_isCancelledLoaded)
                {
                    pnlCancelledWatermark.Visibility = Visibility.Visible;
                    btnSave.IsEnabled = false;
                    btnCancelBill.IsEnabled = false;

                    btnPrint.IsEnabled = false;
                    btnExport.IsEnabled = false;
                }
                else if (!BillPro.Helpers.PermissionGate.CanEdit)
                {
                    pnlCancelledWatermark.Visibility = Visibility.Collapsed;
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
                MessageBox.Show("Error loading entry:\n" + ex.Message, "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLoadingForEdit = false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // EDIT NAVIGATION (from Sales Return Report)
        // ════════════════════════════════════════════════════════════════
        private void OnEditRequested(int entryNo)
        {
            if (_saveMode == "Update" && _editingEntryNo == entryNo)
                return; // already showing this entry

            if (HasUnsavedWork())
            {
                string currentLabel = _editingEntryNo != 0
                    ? $"Sales Return {txtEntryNoPreview.Text}"
                    : "the new sales return you're entering";

                var result = MessageBox.Show(
                    $"You have unsaved changes to {currentLabel}.\n\nSave it before switching to the selected entry?",
                    "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel) return;

                if (result == MessageBoxResult.Yes)
                {
                    BtnSave_Click(this, new RoutedEventArgs());
                    if (HasUnsavedWork()) return; // save failed/validation blocked — stay put
                }
                // No -> fall through and discard
            }

            LoadEntryForEdit(entryNo);
        }

        /// <summary>Called by MainWindow when this view's tab is actually closed
        /// (LayoutDocument.Closed) — see EntryEditNavigator's remarks.</summary>
        public void Dispose()
        {
            EntryEditNavigator.Unsubscribe<int>(EntryChannels.SalesReturnEntry, OnEditRequested);
        }
    }
}