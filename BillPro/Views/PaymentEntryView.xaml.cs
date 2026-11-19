using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BillPro.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class PaymentEntryView : UserControl, IDisposable
    {
        private readonly PaymentRepository _repository;
        private readonly AccountHeadRepository _acHeadRepository;

        private const string CurrentUserId = "ADMIN";

        private bool _isEditMode = false;
        private int _currentPaymentNo = 0;
        private bool _isLoadingRecord = false;

        public PaymentEntryView()
        {
            InitializeComponent();

            _repository = new PaymentRepository();
            _repository.InitializeDatabase();

            _acHeadRepository = new AccountHeadRepository();

            LoadAcHeadCombo();
            LoadBankCombo();

            // Same "picks up changes made in another tab" pattern as
            // ItemRegistrationView's LoadComboBoxes/IsVisibleChanged — a
            // head/bank created or edited elsewhere (Account Head entry,
            // etc.) wasn't showing up here until the app was restarted,
            // since the combos were only ever populated once, in the
            // constructor.
            this.IsVisibleChanged += PaymentEntryView_IsVisibleChanged;

            // Blank, fully locked screen on load — only New / Display / Close
            // are usable until the operator explicitly starts a new entry.
            ClearFields();
            ToggleFields(false);
            BtnSave.IsEnabled = false;
            BtnPrint.IsEnabled = false;
            BtnPdf.IsEnabled = false;
            BtnCancel.IsEnabled = false;

            // Bridge for the Daybook/Ledger Report's "double-click a Payment
            // row to edit it" — see EntryEditNavigator's class doc comment
            // for why this is a Subscribe-in-constructor/Unsubscribe-in-
            // Dispose pair rather than Loaded/Unloaded.
            EntryEditNavigator.Subscribe<int>(EntryChannels.PaymentEntry, OnEditRequested);
        }

        private void PaymentEntryView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
            {
                RefreshComboBoxes();
            }
        }

        /// <summary>Reloads the A/c Head and Bank combos' ItemsSource,
        /// preserving whatever's currently selected (if it still exists) —
        /// same shape as ItemRegistrationView.LoadComboBoxes. Runs every
        /// time this view becomes visible, not just on construction.</summary>
        private void RefreshComboBoxes()
        {
            var currentAcHead = CmbAcHeadName.SelectedValue;
            var currentBankHead = CmbBankAcHead.SelectedValue;

            LoadAcHeadCombo();
            LoadBankCombo();

            if (currentAcHead != null) CmbAcHeadName.SelectedValue = currentAcHead;
            if (currentBankHead != null) CmbBankAcHead.SelectedValue = currentBankHead;
        }

        /// <summary>Called by EntryEditNavigator when a report row is
        /// double-clicked for this Payment No — same "load it for editing"
        /// path as picking it from BtnDisplay's search overlay.</summary>
        private void OnEditRequested(int paymentNo) => LoadPaymentForView(paymentNo);

        public void Dispose()
        {
            EntryEditNavigator.Unsubscribe<int>(EntryChannels.PaymentEntry, OnEditRequested);
            this.IsVisibleChanged -= PaymentEntryView_IsVisibleChanged;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => BtnNew.Focus()));
        }

        private void LoadAcHeadCombo()
        {
            CmbAcHeadName.ItemsSource = _acHeadRepository.GetAllForLookup();
        }

        private void LoadBankCombo()
        {
            // GetBankAcHeadLookups() matches ac_head_master.ac_category_id
            // against the reserved BanksCategoryCode — the same source
            // every Sales/Purchase/Return entry screen's bank combo draws
            // from. The older GetBankAcHeads() matched by category NAME
            // LIKE '%Bank%' instead, which is what this combo was
            // previously wired to; that's the real cause of the "bank
            // field doesn't connect properly" symptom, since a category
            // rename (or a fresh install that never had a name containing
            // "Bank") would leave this combo silently empty.
            CmbBankAcHead.ItemsSource = _acHeadRepository.GetBankAcHeadLookups();
        }

        /// <summary>Enables/disables every data-entry field (read-only display
        /// fields like Address/Mobile/GSTIN/Ledger Balance stay read-only
        /// regardless, since they're always auto-filled, never typed).</summary>
        private void ToggleFields(bool enabled)
        {
            DpPaymentDate.IsEnabled = enabled;
            CmbAcHeadName.IsEnabled = enabled;
            TxtAddress1.IsEnabled = enabled;
            TxtAddress2.IsEnabled = enabled;
            TxtMobile.IsEnabled = enabled;
            TxtGstNo.IsEnabled = enabled;
            TxtNarration.IsEnabled = enabled;
            TxtPaymentAmount.IsEnabled = enabled;
            CmbPaymentMode.IsEnabled = enabled;
            CmbBankAcHead.IsEnabled = enabled;
            TxtBankNarration.IsEnabled = enabled;
        }

        private void ClearFields()
        {
            TxtPaymentNo.Text = _repository.GetNextPaymentNo().ToString();
            DpPaymentDate.SelectedDate = DateTime.Now;
            CmbAcHeadName.SelectedIndex = -1;
            CmbAcHeadName.Text = "";
            TxtAddress1.Clear();
            TxtAddress2.Clear();
            TxtMobile.Clear();
            TxtGstNo.Clear();
            TxtNarration.Clear();
            TxtPaymentAmount.Text = "0";
            TxtLedgerBalance.Clear();
            CmbPaymentMode.SelectedIndex = 0;
            CmbBankAcHead.SelectedIndex = -1;
            CmbBankAcHead.Text = "";
            TxtBankNarration.Clear();
            PnlBank.Visibility = Visibility.Collapsed;

            _isEditMode = false;
            _currentPaymentNo = 0;
            BtnSave.Content = "_Save";

            PnlCancelledWatermark.Visibility = Visibility.Collapsed;
        }

        /// <summary>Shows/hides the CANCELLED watermark and, when cancelled,
        /// locks every field including Save/Update so a cancelled voucher
        /// can't be edited back to life from this screen.</summary>
        private void SetCancelledState(bool cancelled)
        {
            PnlCancelledWatermark.Visibility = cancelled ? Visibility.Visible : Visibility.Collapsed;
            if (cancelled)
            {
                ToggleFields(false);
                BtnSave.IsEnabled = false;
                BtnCancel.IsEnabled = false;
            }
        }

        // ── New ──────────────────────────────────────────────────────────
        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            ClearFields();
            ToggleFields(true);
            BtnSave.IsEnabled = true;
            BtnPrint.IsEnabled = false;
            BtnPdf.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            DpPaymentDate.Focus();
        }

        // ── A/c Head selection: auto-fill address/mobile/GSTIN + ledger balance ──
        private void CmbAcHeadName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingRecord) return;

            if (CmbAcHeadName.SelectedItem is AccountHeadMaster selected)
            {
                TxtAddress1.Text = selected.Address;
                TxtAddress2.Text = "";
                TxtMobile.Text = selected.Mobile;
                TxtGstNo.Text = selected.GstNo;

                TxtLedgerBalance.Text = _acHeadRepository.GetLedgerBalance(selected.AcHeadCode).ToString("0.00");
            }
            else
            {
                TxtAddress1.Clear();
                TxtAddress2.Clear();
                TxtMobile.Clear();
                TxtGstNo.Clear();
                TxtLedgerBalance.Clear();
            }
        }

        private void CmbPaymentMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PnlBank == null) return; // fires once during InitializeComponent before load

            bool isBank = (CmbPaymentMode.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Bank";
            PnlBank.Visibility = isBank ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CmbPaymentMode_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                e.Handled = true; // Prevent default event bubbling

                bool isBank = (CmbPaymentMode.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Bank";

                if (isBank)
                {
                    // Use Dispatcher to ensure layout updates (visibility) are complete before focusing
                    Dispatcher.BeginInvoke(new Action(() => CmbBankAcHead.Focus()), System.Windows.Threading.DispatcherPriority.Input);
                }
                else
                {
                    // Replace 'btnSave' with the actual x:Name of your Save button
                    BtnSave.Focus();
                }
            }
        }

        // ── Save ─────────────────────────────────────────────────────────
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isEditMode && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (DpPaymentDate.SelectedDate == null)
            {
                MessageBox.Show("Please select a valid payment date!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                DpPaymentDate.Focus();
                return;
            }

            if (!(CmbAcHeadName.SelectedItem is AccountHeadMaster selectedHead))
            {
                MessageBox.Show("Please select an A/c Head!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                CmbAcHeadName.Focus();
                return;
            }

            decimal.TryParse(TxtPaymentAmount.Text, out decimal paymentAmount);

            if (paymentAmount == 0)
            {
                MessageBox.Show("Please specify Payment Amount!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtPaymentAmount.Focus();
                return;
            }

            bool isBank = (CmbPaymentMode.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Bank";
            AcHeadLookup bankHead = null;

            if (isBank)
            {
                bankHead = CmbBankAcHead.SelectedItem as AcHeadLookup;
                if (bankHead == null)
                {
                    MessageBox.Show("Please select the Bank A/c Head!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    CmbBankAcHead.Focus();
                    return;
                }
            }

            var entry = new PaymentEntry
            {
                PaymentNo = _currentPaymentNo,
                PaymentDate = DpPaymentDate.SelectedDate.Value.ToString("yyyy-MM-dd"),
                AcHeadCode = selectedHead.AcHeadCode,
                AcHeadName = selectedHead.AcHeadName,
                Narration = TxtNarration.Text.Trim(),
                PaymentAmount = paymentAmount,
                PaymentMode = isBank ? "Bank" : "Cash",
                BankAcHeadCode = bankHead?.AcHeadCode.ToString(),
                BankAcHeadName = bankHead?.AcHeadName,
                BankNarration = isBank ? TxtBankNarration.Text.Trim() : null,
                LedgerBalance = _acHeadRepository.GetLedgerBalance(selectedHead.AcHeadCode)
            };

            try
            {
                if (!_isEditMode)
                {
                    entry.CreatedBy = CurrentUserId;
                    entry.CreatedDate = DateTime.Now.ToString("s");
                    _repository.Save(entry);
                    MessageBox.Show("Payment Saved Successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    entry.LastModifiedBy = CurrentUserId;
                    entry.LastModifiedDate = DateTime.Now.ToString("s");
                    _repository.Update(entry);
                    MessageBox.Show("Payment Updated Successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Record now exists — lock the form down and hand off to Print,
                // instead of clearing straight into another blank entry.
                _currentPaymentNo = entry.PaymentNo;
                _isEditMode = true;
                TxtPaymentNo.Text = entry.PaymentNo.ToString();

                ToggleFields(false);
                BtnSave.IsEnabled = false;
                BtnPrint.IsEnabled = true;
                BtnPdf.IsEnabled = true;
                BtnCancel.IsEnabled = true;
                BtnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving payment:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Display / Load for view ─────────────────────────────────────
        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allItems = _repository.GetAll();
            var config = new SearchConfig
            {
                Title = "Search Payments",
                SearchProperties = new List<string> { "AcHeadName", "Narration" },
                Columns = new List<SearchColumn>
                {
                    new SearchColumn{Header = "Payment No", BindingPath = "PaymentNo", Width = "90"},
                    new SearchColumn{Header = "Date", BindingPath = "PaymentDate", Width = "100"},
                    new SearchColumn{Header = "A/c Head", BindingPath = "AcHeadName", Width = "*"},
                    new SearchColumn{Header = "Amount", BindingPath = "PaymentAmount", Width = "100"},
                },
                TargetOnSelect = TxtNarration
            };
            SearchOverlay.LoadData(allItems, config);
            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is PaymentEntry selected)
            {
                LoadPaymentForView(selected.PaymentNo);
            }
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            BtnDisplay.Focus();
        }

        /// <summary>Loads a saved payment for editing — all fields are
        /// re-enabled, Update/Print/Pdf and (if not already cancelled)
        /// Cancel become available, and focus moves to the date field,
        /// same as ItemRegistrationView.LoadItemForEdit.</summary>
        private void LoadPaymentForView(int paymentNo)
        {
            var entry = _repository.GetByPaymentNo(paymentNo);
            if (entry == null) return;

            _isLoadingRecord = true;
            try
            {
                _isEditMode = true;
                _currentPaymentNo = entry.PaymentNo;

                TxtPaymentNo.Text = entry.PaymentNo.ToString();
                DpPaymentDate.SelectedDate = DateTime.TryParse(entry.PaymentDate, out var d) ? d : (DateTime?)null;

                var head = System.Linq.Enumerable.FirstOrDefault(
                    (IEnumerable<AccountHeadMaster>)CmbAcHeadName.ItemsSource,
                    h => h.AcHeadCode == entry.AcHeadCode);
                CmbAcHeadName.SelectedItem = head;

                TxtAddress1.Text = head?.Address;
                TxtMobile.Text = head?.Mobile;
                TxtGstNo.Text = head?.GstNo;

                TxtNarration.Text = entry.Narration;
                TxtPaymentAmount.Text = entry.PaymentAmount.ToString("0.00");
                TxtLedgerBalance.Text = entry.LedgerBalance.ToString("0.00");

                bool isBank = entry.PaymentMode == "Bank";
                CmbPaymentMode.SelectedIndex = isBank ? 1 : 0;
                PnlBank.Visibility = isBank ? Visibility.Visible : Visibility.Collapsed;

                if (isBank)
                {
                    int.TryParse(entry.BankAcHeadCode, out int bankAcHeadCode);
                    var bankHead = System.Linq.Enumerable.FirstOrDefault(
                        (IEnumerable<AcHeadLookup>)CmbBankAcHead.ItemsSource,
                        h => h.AcHeadCode == bankAcHeadCode);
                    CmbBankAcHead.SelectedItem = bankHead;
                    TxtBankNarration.Text = entry.BankNarration;
                }

                bool canEdit = BillPro.Helpers.PermissionGate.CanEdit;

                ToggleFields(canEdit);
                BtnSave.Content = "_Update";
                BtnSave.IsEnabled = canEdit;
                BtnCancel.IsEnabled = !entry.CancelStatus;
                BtnPrint.IsEnabled = true;
                BtnPdf.IsEnabled = true;

                // Cancelled vouchers stay fully locked (incl. Save/Update) —
                // Print/Pdf remain available so a copy can still be produced.
                SetCancelledState(entry.CancelStatus);

                DpPaymentDate.Focus();
            }
            finally
            {
                _isLoadingRecord = false;
            }
        }

        // ── Cancel payment ───────────────────────────────────────────────
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (_currentPaymentNo == 0) return;

            var ans = MessageBox.Show("The selected payment will be cancelled!\n\nAre you sure?",
                "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (ans == MessageBoxResult.Yes)
            {
                _repository.CancelPayment(_currentPaymentNo);
                MessageBox.Show("Payment Cancelled.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                SetCancelledState(true);
            }
        }

        // ── Print ────────────────────────────────────────────────────────
        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (!(CmbAcHeadName.SelectedItem is AccountHeadMaster head)) return;

            decimal.TryParse(TxtPaymentAmount.Text, out decimal amount);
            bool isBank = (CmbPaymentMode.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Bank";

            var data = new VoucherPrintData
            {
                VoucherType = "PAYMENT",
                VoucherNo = _currentPaymentNo,
                VoucherDate = DpPaymentDate.SelectedDate ?? DateTime.Now,
                AcHeadName = head.AcHeadName,
                Address = head.Address,
                Mobile = head.Mobile,
                GstNo = head.GstNo,
                DescriptionLabel = "Paid Amount",
                Amount = amount,
                Narration = TxtNarration.Text,
                Discount = 0,
                Mode = isBank ? "Bank" : "Cash",
                BankName = (CmbBankAcHead.SelectedItem as AcHeadLookup)?.AcHeadName,
                BankNarration = TxtBankNarration.Text
            };

            VoucherPrintHelper.Print(data);
        }

        // ── PDF export ───────────────────────────────────────────────────
        private void BtnPdf_Click(object sender, RoutedEventArgs e)
        {
            if (!(CmbAcHeadName.SelectedItem is AccountHeadMaster head)) return;

            decimal.TryParse(TxtPaymentAmount.Text, out decimal amount);
            bool isBank = (CmbPaymentMode.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Bank";

            var data = new VoucherPrintData
            {
                VoucherType = "PAYMENT",
                VoucherNo = _currentPaymentNo,
                VoucherDate = DpPaymentDate.SelectedDate ?? DateTime.Now,
                AcHeadName = head.AcHeadName,
                Address = head.Address,
                Mobile = head.Mobile,
                GstNo = head.GstNo,
                DescriptionLabel = "Paid Amount",
                Amount = amount,
                Narration = TxtNarration.Text,
                Discount = 0,
                Mode = isBank ? "Bank" : "Cash",
                BankName = (CmbBankAcHead.SelectedItem as AcHeadLookup)?.AcHeadName,
                BankNarration = TxtBankNarration.Text
            };

            VoucherPdfHelper.ExportPdf(data);
        }
    }
}