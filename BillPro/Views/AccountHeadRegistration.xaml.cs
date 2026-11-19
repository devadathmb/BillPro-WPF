using AvalonDock.Layout;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BillPro.Controls;
using BillPro.Models;
using BillPro.Repositories;
using BillPro.Helpers;

namespace BillPro.Views
{
    public partial class AccountHeadRegistration : UserControl
    {
        private readonly AccountHeadRepository _repository;
        private readonly AccountCategoryRepository _categoryRepository;

        private string _saveMode = "Save";
        private int _currentRecordId = 0;

        // Tracks whether the record currently loaded into the form is
        // system-reserved (code < 1000) — the only case that also blocks
        // Save, on top of Delete. Re-evaluated every time a record is
        // loaded via LoadItemForEdit, and cleared on New.
        private bool _isCurrentReserved = false;

        public AccountHeadRegistration()
        {
            InitializeComponent();
            _repository = new AccountHeadRepository();

            this.IsVisibleChanged += AccountHeadRegistrationView_IsVisibleChanged;

            // Initialize your category repo to pull data for the combobox
            _categoryRepository = new AccountCategoryRepository();

            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;

            LoadComboBoxes();
        }

        private void AccountHeadRegistrationView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
            {
                LoadComboBoxes();
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                btnNew.Focus();
            }));
        }

        private void LoadComboBoxes()
        {
            // Preserve whatever's currently selected before we swap the
            // ItemsSource out from under it — same pattern as
            // ItemRegistrationView.LoadComboBoxes(). Without this, tabbing
            // into this view (which triggers IsVisibleChanged) resets the
            // category combo on every visit, even mid-edit.
            var currentCategory = CmbAcCategoryName.SelectedValue;

            var categories = _categoryRepository.GetAll();

            CmbAcCategoryName.ItemsSource = categories;

            // Ensure these match the properties in your AccountCategory model!
            CmbAcCategoryName.DisplayMemberPath = "CategoryName";
            CmbAcCategoryName.SelectedValuePath = "CategoryCode";

            if (currentCategory != null) CmbAcCategoryName.SelectedValue = currentCategory;
        }

        private void ToggleFields(bool isEnabled)
        {
            TxtAcHeadCode.IsEnabled = isEnabled;
            TxtAcHeadName.IsEnabled = isEnabled;
            CmbAcCategoryName.IsEnabled = isEnabled;
            CmbAcGroup.IsEnabled = isEnabled;
            TxtOpeningCredit.IsEnabled = isEnabled;
            TxtOpeningDebit.IsEnabled = isEnabled;
            btnSave.IsEnabled = isEnabled;
        }

        private void ClearFields()
        {
            TxtAcHeadCode.Clear();
            TxtAcHeadName.Clear();
            //CmbAcCategoryName.SelectedIndex = -1;
            //CmbAcCategoryName.Text = "";
            CmbAcGroup.SelectedIndex = 0;
            TxtOpeningCredit.Text = "0";
            TxtOpeningDebit.Text = "0";
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            // Category list may have changed (add/edit/delete) since this
            // view was constructed or last used — refresh before offering
            // a fresh combo, otherwise a newly added category wouldn't be
            // selectable yet.

            ToggleFields(true);
            ClearFields();


            // 1. Fetch the next available code
            TxtAcHeadCode.Text = _repository.GetNextAcHeadCode();

            _currentRecordId = 0;
            _isCurrentReserved = false;
            _saveMode = "Save";
            btnSave.Content = "_Save";
            btnDelete.IsEnabled = false;

            TxtAcHeadName.Focus();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (!FormValidator.ValidateRequiredFields(
                (TxtAcHeadCode, "Account Head Code"),
                (TxtAcHeadName, "Account Head Name"),
                (CmbAcCategoryName, "Account Category Name"),
                (CmbAcGroup, "Account Group")))
                return;

            // Defense in depth: LoadItemForEdit already disables btnSave
            // for a reserved head, but guard the write itself too in case
            // that state was ever bypassed. Being in use elsewhere does
            // NOT block editing — only deletion — so no in-use check here.
            if (_saveMode == "Update" && _isCurrentReserved)
            {
                MessageBox.Show("This is a system-reserved Account Head and can't be edited.",
                    "Reserved", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int.TryParse(CmbAcCategoryName.SelectedValue?.ToString(), out int categoryId);
            decimal.TryParse(TxtOpeningCredit.Text, out decimal openingCredit);
            decimal.TryParse(TxtOpeningDebit.Text, out decimal openingDebit);

            if ((openingCredit != 0 || openingDebit != 0) && CmbAcGroup.SelectedIndex != 0)
            {
                var proceed = MessageBox.Show(
                    "Opening Credit/Debit is only used for Balance Sheet account heads.\n\n" +
                    "Entering it for this group means it won't appear in Opening Balance Entry, " +
                    "and the trial balance will not get balanced.\n\nDo you still want to save?",
                    "Opening Balance Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (proceed != MessageBoxResult.Yes)
                    return;
            }

            var acHead = new AccountHeadMaster
            {
                Id = _currentRecordId,
                AcHeadCode = TxtAcHeadCode.Text.Trim(),
                AcHeadName = TxtAcHeadName.Text.Trim(),
                AcCategoryId = categoryId,
                AcGroupId = CmbAcGroup.SelectedIndex,
                OpeningCredit = openingCredit,
                OpeningDebit = openingDebit,
                Address = "",
                Phone = "",
                Mobile = ""
            };

            try
            {
                if (_saveMode == "Save")
                {
                    _repository.Save(acHead);
                    MessageBox.Show("Record Saved Successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    _repository.UpdateAccountHead(acHead);
                    MessageBox.Show("Record Updated Successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Post-save UI reset (only executes if save/update succeeds)
                ToggleFields(false);
                btnDelete.IsEnabled = false; // Prevents deletion of the locked record
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving record: " + ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Fields remain enabled so the user can correct the error
            }
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allItems = _repository.GetAll();
            var config = new SearchConfig
            {
                Title = "Search Account Heads",
                SearchProperties = new List<string> { "AcHeadCode", "AcHeadName" },
                Columns = new List<SearchColumn>
                {
                    new SearchColumn{Header = "Code", BindingPath = "AcHeadCode", Width = "80"},
                    new SearchColumn{Header = "Account Head Name", BindingPath = "AcHeadName", Width = "*"},
                    new SearchColumn{Header = "Debit", BindingPath = "OpeningDebit", Width = "80"},
                    new SearchColumn{Header = "Credit", BindingPath = "OpeningCredit", Width = "80"},
                },
                TargetOnSelect = TxtAcHeadName
            };
            SearchOverlay.LoadData(allItems, config);
            SearchOverlay.Visibility = Visibility.Visible;

        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is AccountHeadMaster selectedItem)
            {
                LoadItemForEdit(selectedItem.AcHeadCode);
            }
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            btnDisplay.Focus();
        }

        private void LoadItemForEdit(string acHeadCode)
        {
            try
            {
                var acHead = _repository.GetById("ac_head_code", acHeadCode);
                if (acHead == null) return;

                // Refresh BEFORE setting SelectedValue below — otherwise a
                // category added/renamed after this view was constructed
                // wouldn't be in the combo's ItemsSource yet, SelectedValue
                // would silently fail to match anything, and the combobox
                // would show blank even though acHead.AcCategoryId is correct.
                LoadComboBoxes();

                _isCurrentReserved = _repository.IsReservedHeadCode(acHead.AcHeadCode);
                bool isInUse = _repository.IsAcHeadInUse(acHead.AcHeadCode);

                // Reserved heads are locked from both editing and deleting.
                // In-use (but not reserved) heads are still fully editable —
                // only deleting them is blocked, since deleting is what
                // would actually orphan the records referencing them.
                // Nothing here pops a message box on simple load/display —
                // the buttons just quietly reflect what's allowed.
                ToggleFields(!_isCurrentReserved);
                _saveMode = "Update";

                _currentRecordId = acHead.Id;

                TxtAcHeadCode.Text = acHead.AcHeadCode;
                TxtAcHeadCode.IsEnabled = false;
                TxtAcHeadName.Text = acHead.AcHeadName;
                CmbAcCategoryName.SelectedValue = acHead.AcCategoryId;
                CmbAcGroup.SelectedIndex = acHead.AcGroupId;
                TxtOpeningCredit.Text = acHead.OpeningCredit.ToString();
                TxtOpeningDebit.Text = acHead.OpeningDebit.ToString();

                btnSave.Content = "_Update";
                btnSave.IsEnabled = !_isCurrentReserved;
                btnDelete.IsEnabled = !_isCurrentReserved && !isInUse;
            }
            catch (Exception e)
            {
                MessageBox.Show("Error Loading Record:\n" + e.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (string.IsNullOrWhiteSpace(TxtAcHeadCode.Text)) return;

            string code = TxtAcHeadCode.Text.Trim();

            // Defense in depth alongside LoadItemForEdit's button state —
            // see BtnSave_Click for the equivalent on the Save path.
            if (_repository.IsReservedHeadCode(code))
            {
                MessageBox.Show("This is a system-reserved Account Head and can't be deleted.",
                    "Reserved", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_repository.IsAcHeadInUse(code))
            {
                MessageBox.Show("This Account Head is already in use in one or more entries and can't be deleted.",
                    "In Use", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show("Are you sure you want to delete this record?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _repository.Delete("ac_head_code", code);
                MessageBox.Show("Record Deleted Successfully.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);

                _currentRecordId = 0;
                _isCurrentReserved = false;
                ClearFields();
                ToggleFields(false);

                btnSave.Content = "_Save";
                btnDelete.IsEnabled = false;
                btnNew.Focus();
            }
        }
    }
}