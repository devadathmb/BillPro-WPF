using System;
using System.Windows;
using System.Windows.Controls;
using BillPro.Models;
using BillPro.Repositories;
using BillPro.Helpers;

namespace BillPro.Views
{
    public partial class AccountCategoryRegistrationView : UserControl
    {
        private readonly AccountCategoryRepository _repository;
        private const string CurrentUserId = "ADMIN";

        public AccountCategoryRegistrationView()
        {
            InitializeComponent();
            _repository = new AccountCategoryRepository();
            _repository.InitializeDatabase();
            LoadCategories();
            this.Loaded += (sender, args) =>
            {
                Dispatcher.BeginInvoke(new Action(() => txtCategoryName?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            };
        }

        private void LoadCategories()
        {
            try
            {
                lstCategories.ItemsSource = _repository.GetAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Loading Categories:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!FormValidator.ValidateRequiredFields((txtCategoryName, "Category Name"))) return;

                string catName = txtCategoryName.Text.Trim();

                if (_repository.CategoryExists(catName))
                {
                    MessageBox.Show("This category is already existing!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    txtCategoryName.Focus();
                    return;
                }

                var newCategory = new AccountCategory
                {
                    CategoryCode = _repository.GetNextCategoryCode(),
                    CategoryName = catName,
                    CreatedBy = CurrentUserId,
                    CreatedDate = DateTime.Now.ToString("s")
                };

                _repository.AddCategory(newCategory);
                ClearForm();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Adding Category:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireEdit()) return;

            try
            {
                if (lstCategories.SelectedItem is AccountCategory selected)
                {
                    // Reserved (system) categories are locked from editing
                    // entirely — this is the only edit block. A category
                    // that's merely in use by existing account heads is
                    // still fine to rename; only deleting it is blocked
                    // (see BtnDelete_Click), since renaming doesn't orphan
                    // anything the way deleting would.
                    if (selected.CategoryCode <= 100)
                    {
                        MessageBox.Show("You cannot delete pre set Categories!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (!FormValidator.ValidateRequiredFields((txtCategoryName, "Category Name"))) return;

                    selected.CategoryName = txtCategoryName.Text.Trim();
                    selected.LastModifiedBy = CurrentUserId;
                    selected.LastModifiedDate = DateTime.Now.ToString("s");

                    _repository.UpdateCategory(selected);
                    ClearForm();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Editing Category:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;

            try
            {
                if (lstCategories.SelectedItem is AccountCategory selected)
                {
                    if (selected.CategoryCode <= 100)
                    {
                        MessageBox.Show("Only Programmers have the right to delete this Account Category!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (_repository.IsCategoryInUse(selected.CategoryCode))
                    {
                        MessageBox.Show("Can't Delete this Account Category!\n\nCategory already used in Account Master", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (MessageBox.Show("The selected item will be permanently deleted!\n\nAre you sure?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        _repository.Delete("category_code", selected.CategoryCode);
                        ClearForm();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Deleting Category:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearForm()
        {
            txtCategoryName.Clear();
            LoadCategories();
            txtCategoryName.Focus();
        }

        private void lstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstCategories.SelectedItem is AccountCategory selected)
            {
                txtCategoryName.Text = selected.CategoryName;
            }
        }
    }
}