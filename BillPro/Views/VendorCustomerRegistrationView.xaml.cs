using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;
using BillPro.Controls;

namespace BillPro.Views
{
    public partial class VendorCustomerRegistrationView : UserControl
    {
        private readonly VendorCustomerRepository _repository;
        private string _saveMode = "Save";
        private string _currentCustomerCode = "";

        public VendorCustomerRegistrationView()
        {
            try
            {
                InitializeComponent();
                SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
                SearchOverlay.Closed += SearchOverlay_Closed;

                this.Loaded += (sender, args) =>
                {
                    Dispatcher.BeginInvoke(new Action(() => btnNew?.Focus()),
                        System.Windows.Threading.DispatcherPriority.Input);
                };

                _repository = new VendorCustomerRepository();
                _repository.InitializeDatabase();

                DisableFields();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ERROR:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EnableFields()
        {
            txtName.IsEnabled = true;
            txtAddress1.IsEnabled = true;
            txtAddress2.IsEnabled = true;
            txtAddress3.IsEnabled = true;
            txtPlace.IsEnabled = true;
            txtPin.IsEnabled = true;
            txtPhone.IsEnabled = true;
            txtMobile.IsEnabled = true;
            txtFax.IsEnabled = true;
            txtEmail.IsEnabled = true;
            txtGst.IsEnabled = true;
            txtContactPerson.IsEnabled = true;
            cmbCategory.IsEnabled = true;
        }

        private void DisableFields()
        {
            txtName.IsEnabled = false;
            txtAddress1.IsEnabled = false;
            txtAddress2.IsEnabled = false;
            txtAddress3.IsEnabled = false;
            txtPlace.IsEnabled = false;
            txtPin.IsEnabled = false;
            txtPhone.IsEnabled = false;
            txtMobile.IsEnabled = false;
            txtFax.IsEnabled = false;
            txtEmail.IsEnabled = false;
            txtGst.IsEnabled = false;
            txtContactPerson.IsEnabled = false;
            cmbCategory.IsEnabled = false;
        }

        private void ClearFields()
        {
            txtName.Text = "";
            txtAddress1.Text = "";
            txtAddress2.Text = "";
            txtAddress3.Text = "";
            txtPlace.Text = "";
            txtPin.Text = "";
            txtPhone.Text = "";
            txtMobile.Text = "";
            txtFax.Text = "";
            txtEmail.Text = "";
            txtGst.Text = "";
            txtContactPerson.Text = "";
            cmbCategory.SelectedIndex = 0;
            _currentCustomerCode = "";
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            EnableFields();
            ClearFields();
            _saveMode = "Save";
            _currentCustomerCode = _repository.GetNextCustomerCode();

            btnSave.Content = "Save";
            btnSave.IsEnabled = true;
            btnDelete.IsEnabled = false;
            txtName.Focus();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var allCustomers = _repository.GetAll();

                var config = new SearchConfig
                {
                    Title = "Search Vendor / Customer",
                    SearchProperties = new List<string> { "CustomerName", "Phone", "Mobile", "Address1" },
                    Columns = new List<SearchColumn>
                    {
                        new SearchColumn { Header = "Name",     BindingPath = "CustomerName", Width = "*"   },
                        new SearchColumn { Header = "Category", BindingPath = "Category",     Width = "100" },
                        new SearchColumn { Header = "Address",  BindingPath = "Address1",     Width = "250" },
                        new SearchColumn { Header = "Phone",    BindingPath = "Phone",         Width = "100" },
                        new SearchColumn { Header = "Mobile",   BindingPath = "Mobile",        Width = "100" }
                    },
                    TargetOnSelect = txtName
                };

                SearchOverlay.LoadData(allCustomers, config);
                SearchOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading records:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is VendorCustomerMaster selectedCustomer)
                LoadCustomerForEdit(selectedCustomer.CustomerCode);
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            btnDisplay.Focus();
        }

        private void LoadCustomerForEdit(string customerCode)
        {
            try
            {
                var customer = _repository.GetById("customer_code", customerCode);
                if (customer == null) return;

                EnableFields();
                _saveMode = "Update";
                _currentCustomerCode = customer.CustomerCode;

                txtName.Text = customer.CustomerName;
                txtAddress1.Text = customer.Address1;
                txtAddress2.Text = customer.Address2;
                txtAddress3.Text = customer.Address3;
                txtPlace.Text = customer.Place;
                txtPin.Text = customer.Pin;
                txtPhone.Text = customer.Phone;
                txtMobile.Text = customer.Mobile;
                txtFax.Text = customer.Fax;
                txtEmail.Text = customer.Email;
                txtGst.Text = customer.GstNo;
                txtContactPerson.Text = customer.ContactPerson;

                foreach (ComboBoxItem item in cmbCategory.Items)
                {
                    if (item.Content.ToString() == customer.Category)
                    {
                        cmbCategory.SelectedItem = item;
                        break;
                    }
                }

                btnSave.Content = "Update";
                btnSave.IsEnabled = true;
                btnDelete.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading record:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (!FormValidator.ValidateRequiredFields((txtName, "Name")))
                return;

            if (cmbCategory.SelectedItem == null)
            {
                MessageBox.Show("Please select Category!", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbCategory.Focus();
                return;
            }

            try
            {
                var customer = new VendorCustomerMaster
                {
                    CustomerCode = _currentCustomerCode,
                    CustomerName = txtName.Text.Trim(),
                    Address1 = txtAddress1.Text.Trim(),
                    Address2 = txtAddress2.Text.Trim(),
                    Address3 = txtAddress3.Text.Trim(),
                    Place = txtPlace.Text.Trim(),
                    Pin = txtPin.Text.Trim(),
                    Phone = txtPhone.Text.Trim(),
                    Mobile = txtMobile.Text.Trim(),
                    Fax = txtFax.Text.Trim(),
                    Email = txtEmail.Text.Trim(),
                    GstNo = txtGst.Text.Trim(),
                    ContactPerson = txtContactPerson.Text.Trim(),
                    Category = (cmbCategory.SelectedItem as ComboBoxItem)?.Content.ToString(),
                };

                if (_saveMode == "Save")
                {
                    if (_repository.CustomerExists(customer.CustomerName))
                    {
                        MessageBox.Show("Vendor/Customer Name already exists!", "Information",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        txtName.Focus();
                        return;
                    }

                    customer.CreatedBy = "USER";
                    customer.CreatedDate = DateTime.Now.ToString("s");

                    // AddCustomer handles both DBs internally:
                    // generates ac_head_code → writes ac_head_master → writes vendor_customer_master
                    _repository.AddCustomer(customer);

                    MessageBox.Show("Record saved successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    customer.LastModifiedBy = "USER";
                    customer.LastModifiedDate = DateTime.Now.ToString("s");

                    // Carry the stored AcHeadCode forward so UpdateCustomer can sync accounts DB
                    var existing = _repository.GetById("customer_code", _currentCustomerCode);
                    customer.AcHeadCode = existing?.AcHeadCode;

                    _repository.UpdateCustomer(customer);

                    MessageBox.Show("Record updated successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                ClearFields();
                DisableFields();
                btnSave.IsEnabled = false;
                btnDelete.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving record:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (string.IsNullOrEmpty(_currentCustomerCode)) return;

            try
            {
                if (MessageBox.Show(
                        "The selected record will be permanently deleted!\n\nAre you sure?",
                        "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    == MessageBoxResult.Yes)
                {
                    _repository.Delete("customer_code", _currentCustomerCode);

                    ClearFields();
                    DisableFields();
                    btnSave.IsEnabled = false;
                    btnDelete.IsEnabled = false;
                    btnNew.Focus();

                    MessageBox.Show("Record deleted successfully.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error deleting record:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Field_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string nextName = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(nextName)) return;
            (FindName(nextName) as FrameworkElement)?.Focus();
        }

        private void txtMobile_TextChanged(object sender, TextChangedEventArgs e)
        {
        }
    }
}