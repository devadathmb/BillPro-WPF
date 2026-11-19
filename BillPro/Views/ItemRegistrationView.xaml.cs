using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BillPro.Models;
using BillPro.Repositories;
using BillPro.Helpers;
using System.Collections.Generic;

namespace BillPro.Views
{
    public partial class ItemRegistrationView : UserControl
    {
        private readonly ItemMasterRepository _repository;
        private readonly UqcRepository _uqcRepository;
        private readonly HSNCodeRepository _hsnRepository;
        private string _saveMode = "Save";

        public ItemRegistrationView()
        {
            try
            {
                InitializeComponent();

                SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
                SearchOverlay.Closed += SearchOverlay_Closed;

                this.IsVisibleChanged += ItemRegistrationView_IsVisibleChanged;

                this.Loaded += (sender, args) =>
                {
                    Dispatcher.BeginInvoke(new Action(() => btnNew?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
                };
                SQLitePCL.Batteries.Init();

                _repository = new ItemMasterRepository();
                _repository.InitializeDatabase();

                _uqcRepository = new UqcRepository();
                _uqcRepository.InitializeAndSeed();

                _hsnRepository = new HSNCodeRepository();

                LoadComboBoxes();
                DisableFields();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"CRITICAL STARTUP ERROR:\n\n{ex.Message}\n\n{ex.InnerException?.Message}", "Fatal Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ItemRegistrationView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
            {
                LoadComboBoxes();
            }
        }

        private void LoadComboBoxes()
        {
            var currentCategory = cmbCategoryName.SelectedValue;
            var currentCompany = cmbCompanyName.SelectedValue;
            var currentUqc = cmbUqc.SelectedValue;
            var currentHsn = cmbHsnCode.SelectedValue;

            cmbCategoryName.ItemsSource = _repository.GetCategories();
            cmbCompanyName.ItemsSource = _repository.GetCompanies();
            cmbUqc.ItemsSource = _uqcRepository.GetAll();
            cmbHsnCode.ItemsSource = _hsnRepository.GetAll();

            if (currentCategory != null) cmbCategoryName.SelectedValue = currentCategory;
            if (currentCompany != null) cmbCompanyName.SelectedValue = currentCompany;
            if (currentUqc != null) cmbUqc.SelectedValue = currentUqc;
            if (currentHsn != null) cmbHsnCode.SelectedValue = currentHsn;
        }

        private void EnableFields()
        {
            txtItemCode.IsEnabled = true;
            cmbHsnCode.IsEnabled = true;
            txtItemName.IsEnabled = true;
            cmbCategoryName.IsEnabled = true;
            cmbCompanyName.IsEnabled = true;
            txtMrp.IsEnabled = true;
            txtUnitPriceSales.IsEnabled = true;
            txtUnitPricePurchase.IsEnabled = true;
            txtTaxRate.IsEnabled = true;
            txtQtyPouch.IsEnabled = true;
            txtPouchBox.IsEnabled = true;
            txtReorderLevel.IsEnabled = true;
            txtRackName.IsEnabled = true;
            cmbUqc.IsEnabled = true;
            cmbCategory.IsEnabled = true;
            cmbIsActive.IsEnabled = true;
        }

        private void DisableFields()
        {
            txtItemCode.IsEnabled = false;
            cmbHsnCode.IsEnabled = false;
            txtItemName.IsEnabled = false;
            cmbCategoryName.IsEnabled = false;
            cmbCompanyName.IsEnabled = false;
            txtMrp.IsEnabled = false;
            txtUnitPriceSales.IsEnabled = false;
            txtUnitPricePurchase.IsEnabled = false;
            txtTaxRate.IsEnabled = false;
            txtQtyPouch.IsEnabled = false;
            txtPouchBox.IsEnabled = false;
            txtReorderLevel.IsEnabled = false;
            txtRackName.IsEnabled = false;
            cmbUqc.IsEnabled = false;
            cmbCategory.IsEnabled = false;
            cmbIsActive.IsEnabled = false;
        }

        private void ClearFields()
        {
            txtItemCode.Text = "";
            //cmbHsnCode.SelectedIndex = -1;
            //cmbHsnCode.Text = "";
            txtHSNDescription.Text = "";
            txtItemName.Text = "";
            //cmbCategoryName.SelectedIndex = -1;
            //cmbCompanyName.SelectedIndex = -1;
            txtMrp.Text = "0";
            txtUnitPriceSales.Text = "0";
            txtUnitPricePurchase.Text = "0";
            txtTaxRate.Text = "";
            txtQtyPouch.Text = "0";
            txtPouchBox.Text = "0";
            txtReorderLevel.Text = "";
            txtRackName.Text = "";
            //cmbUqc.SelectedIndex = -1;
            cmbCategory.SelectedIndex = 0;
            cmbIsActive.SelectedIndex = 0;
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            EnableFields();
            ClearFields();
            _saveMode = "Save";

            txtItemCode.Text = _repository.GetNextItemCode();

            btnSave.Content = "Save";
            btnSave.IsEnabled = true;
            btnDelete.IsEnabled = false;
            cmbHsnCode.Focus();
        }

        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allItems = _repository.GetAll();

            var config = new SearchConfig
            {
                Title = "Search Item Master",
                SearchProperties = new List<string> { "ItemCode", "ItemName" },
                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "Code", BindingPath = "ItemCode", Width = "100" },
                    new SearchColumn { Header = "Item Name", BindingPath = "ItemName", Width = "*" },
                    new SearchColumn { Header = "MRP", BindingPath = "Mrp", Width = "100" },
                    new SearchColumn { Header = "Is Active", BindingPath = "IsActive", Width = "100" }

                },
                TargetOnSelect = txtItemName
            };

            SearchOverlay.LoadData(allItems, config);
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is ItemMaster selectedItem)
            {
                LoadItemForEdit(selectedItem.ItemCode);
            }
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            btnDisplay.Focus();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saveMode == "Update" && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (!FormValidator.ValidateRequiredFields(
                (txtItemCode, "Item Code"),
                (cmbHsnCode, "HSN Code"),
                (txtItemName, "Item Name"),
                (cmbCategoryName, "Item Category"),
                (cmbCompanyName, "Company"),
                (cmbUqc, "Sales Unit")
                ))
            {
                return;
            }

            string itemCode = txtItemCode.Text.Trim().ToUpper();

            try
            {
                var item = new ItemMaster
                {
                    ItemCode = itemCode,
                    HsnCode = cmbHsnCode.SelectedValue.ToString(),
                    ItemName = txtItemName.Text.Trim(),
                    ItemCategoryCode = cmbCategoryName.SelectedValue.ToString(),
                    CompanyCode = cmbCompanyName.SelectedValue.ToString(),
                    Mrp = ToDouble(txtMrp.Text),
                    UnitPriceSales = ToDouble(txtUnitPriceSales.Text),
                    UnitPricePurchase = ToDouble(txtUnitPricePurchase.Text),
                    TaxRate = ToDouble(txtTaxRate.Text),
                    QtyPouch = ToDouble(txtQtyPouch.Text),
                    PouchBox = ToDouble(txtPouchBox.Text),
                    ReOrderLevel = ToDouble(txtReorderLevel.Text),
                    Rack = txtRackName.Text.Trim(),
                    SalesUnit = cmbUqc.SelectedValue?.ToString() ?? "",
                    Category = cmbCategory.SelectedIndex,
                    IsActive = cmbIsActive.SelectedIndex
                };

                if (_saveMode == "Save")
                {
                    if (_repository.ItemExists(itemCode))
                    {
                        MessageBox.Show("Item code already exists!\nPlease use a different code.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        txtItemCode.Focus();
                        return;
                    }
                    item.CreatedBy = "USER";
                    item.CreatedDate = DateTime.Now.ToString("s");
                    _repository.AddItem(item);
                    MessageBox.Show("Item saved successfully!\n\nUse Opening Stock Entry to record starting quantity/MRP for this item.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    item.LastModifiedBy = "USER";
                    item.LastModifiedDate = DateTime.Now.ToString("s");
                    _repository.UpdateItem(item);
                    MessageBox.Show("Item updated successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                DisableFields();
                btnSave.IsEnabled = false;
                btnDelete.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving record:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            string itemCode = txtItemCode.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(itemCode)) return;

            try
            {
                // Was: _repository.GetOpeningStock(itemCode) != 0
                // Now checks the ledger directly — any movement at all (opening/purchase/sale) blocks delete.
                if (_repository.GetCurrentStock(itemCode) != 0)
                {
                    MessageBox.Show("Can't delete! Stock movement exists for this item.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (_repository.IsItemInUse(itemCode))
                {
                    MessageBox.Show("Can't delete! Item is already used in other tables.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (MessageBox.Show("The selected item will be permanently deleted!\n\nAre you sure?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _repository.Delete("item_code", itemCode);

                    ClearFields();
                    DisableFields();
                    btnSave.IsEnabled = false;
                    btnDelete.IsEnabled = false;
                    MessageBox.Show("Item deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    btnNew.Focus();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error deleting record:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void LoadItemForEdit(string itemCode)
        {
            try
            {
                var item = _repository.GetById("item_code", itemCode);

                if (item == null) return;

                EnableFields();
                _saveMode = "Update";

                txtItemCode.Text = item.ItemCode;
                txtItemCode.IsEnabled = false;
                cmbHsnCode.SelectedValue = item.HsnCode;
                txtItemName.Text = item.ItemName;
                txtMrp.Text = item.Mrp.ToString();
                txtUnitPriceSales.Text = item.UnitPriceSales.ToString();
                txtUnitPricePurchase.Text = item.UnitPricePurchase.ToString();
                txtTaxRate.Text = item.TaxRate.ToString();
                txtQtyPouch.Text = item.QtyPouch.ToString();
                txtPouchBox.Text = item.PouchBox.ToString();
                txtReorderLevel.Text = item.ReOrderLevel.ToString();
                txtRackName.Text = item.Rack;

                cmbCategoryName.SelectedValue = item.ItemCategoryCode;
                cmbCompanyName.SelectedValue = item.CompanyCode;
                cmbUqc.SelectedValue = item.SalesUnit;
                cmbCategory.SelectedIndex = item.Category;
                cmbIsActive.SelectedIndex = item.IsActive;

                btnSave.Content = "Update";
                btnSave.IsEnabled = true;
                btnDelete.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading item:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private double ToDouble(string text)
        {
            double.TryParse(text, out double val);
            return val;
        }

        private void CmbHsnCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbHsnCode.SelectedItem != null)
            {
                var selectedItem = cmbHsnCode.SelectedItem;
                var taxProp = selectedItem.GetType().GetProperty("TaxRate");
                var hsnDesc = selectedItem.GetType().GetProperty("ItemDescription");

                if (taxProp != null)
                {
                    txtTaxRate.Text = taxProp.GetValue(selectedItem)?.ToString() ?? "0";
                }

                if (hsnDesc != null)
                {
                    txtHSNDescription.Text = hsnDesc.GetValue(selectedItem)?.ToString() ?? "";
                }
            }
        }
    }
}