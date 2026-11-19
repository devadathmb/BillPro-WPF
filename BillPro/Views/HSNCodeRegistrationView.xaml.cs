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
    public partial class HSNCodeRegistrationView : UserControl
    {
        private readonly HSNCodeRepository _repository;
        private string _saveMode = "Save";

        // 1. ADDED: Track the hidden Primary Key
        private int _currentRecordId = 0;

        public HSNCodeRegistrationView()
        {
            InitializeComponent();
            _repository = new HSNCodeRepository();

            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;
        }


        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                btnNew.Focus();
            }));
        }

        private void ToggleFields(bool isEnabled)
        {
            txtHsnCode.IsEnabled = isEnabled;
            txtDescription.IsEnabled = isEnabled;
            txtTaxRate.IsEnabled = isEnabled;
            btnSave.IsEnabled = isEnabled;
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            ToggleFields(true);
            txtHsnCode.Clear();
            txtDescription.Clear();
            txtTaxRate.Clear();

            // 2. RESET: Clear the ID when making a new record
            _currentRecordId = 0;

            _saveMode = "Save";
            btnSave.Content = "_Save";
            btnDelete.IsEnabled = false;

            txtHsnCode.Focus();
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is HSNCodeMaster selectedItem)
            {
                LoadItemForEdit(selectedItem.HsnCode);
            }
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            btnDisplay.Focus();
        }

        private void LoadItemForEdit(string hsnCode)
        {
            try
            {
                // Correctly uses the snake_case DB column for the SQL query
                var code = _repository.GetById("hsn_code", hsnCode);
                if (code == null) return;

                ToggleFields(true);
                txtHsnCode.IsEnabled = false;
                _saveMode = "Update";

                // 3. STORE IT: Save the AutoNumber ID in the background
                _currentRecordId = code.Id;

                txtHsnCode.Text = code.HsnCode;
                txtDescription.Text = code.ItemDescription;
                txtTaxRate.Text = code.TaxRate.ToString();

                btnSave.Content = "_Update";
                btnSave.IsEnabled = true;
                // Delete stays enabled here — the in-use check runs on click
                // (see BtnDelete_Click) and shows a message instead, same as
                // AccountCategoryRegistrationView / ItemRegistrationView.
                btnDelete.IsEnabled = true;
                txtDescription.Focus();

            }
            catch (Exception e)
            {
                MessageBox.Show("Error Loading Item:\n" + e.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (!FormValidator.ValidateRequiredFields((txtHsnCode, "HSN Code")))
                return;

            decimal.TryParse(txtTaxRate.Text, out decimal taxRate);

            var hsnCode = new HSNCodeMaster
            {
                // 4. USE IT: Pass the ID back so the Repository knows to UPDATE instead of INSERT
                Id = _currentRecordId,

                HsnCode = txtHsnCode.Text.Trim(),
                ItemDescription = txtDescription.Text.Trim(),
                TaxRate = taxRate
            };

            try
            {
                _repository.Save(hsnCode);

                string successMsg = _saveMode == "Update" ? "Record Updated Successfully." : "Record Saved Successfully.";
                MessageBox.Show(successMsg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // 5. RESET: Clean up after successful save
                _currentRecordId = 0;

                txtHsnCode.Clear();
                txtDescription.Clear();
                txtTaxRate.Clear();
                ToggleFields(false);

                btnSave.Content = "_Save";
                btnDelete.IsEnabled = false;

                btnNew.Focus();
            }
            catch (Exception ex)
            {
                // Catching Unique Constraint errors in case they type an HSN code that already exists
                MessageBox.Show("Error saving record. Make sure the HSN Code is unique.\n\nDetails: " + ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allItems = _repository.GetAll();
            var config = new SearchConfig
            {
                Title = "Search HSN Codes",
                // FIXED: These MUST match your C# Property names in HSNCodeMaster.cs, NOT the database columns!
                SearchProperties = new List<string> { "HsnCode", "ItemDescription" },
                Columns = new List<SearchColumn>
                {
                    new SearchColumn{Header = "HSN Code", BindingPath = "HsnCode", Width = "100"},
                    new SearchColumn{Header = "Description", BindingPath = "ItemDescription", Width = "*"},
                    new SearchColumn{Header = "Tax Rate", BindingPath = "TaxRate", Width = "100"},
                },
                TargetOnSelect = txtHsnCode
            };
            SearchOverlay.LoadData(allItems, config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (string.IsNullOrWhiteSpace(txtHsnCode.Text)) return;

            string hsnCode = txtHsnCode.Text.Trim();

            // Defense in depth alongside LoadItemForEdit's button state above —
            // same "in use" pattern as ItemMasterRepository.IsItemInUse /
            // AccountCategoryRepository.IsCategoryInUse.
            if (_repository.IsHsnCodeInUse(hsnCode))
            {
                MessageBox.Show("Can't delete! This HSN Code is used by one or more items.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show("Are you sure you want to delete this record?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // Correctly uses the snake_case DB column for the SQL query
                _repository.Delete("hsn_code", hsnCode);
                MessageBox.Show("Record Deleted Successfully.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);

                // Reset ID just to be safe
                _currentRecordId = 0;

                txtHsnCode.Clear();
                txtDescription.Clear();
                txtTaxRate.Clear();
                ToggleFields(false);

                btnSave.Content = "_Save";
                btnDelete.IsEnabled = false;

                btnNew.Focus();
            }
        }
    }
}