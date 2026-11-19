using System;
using System.Windows;
using System.Windows.Controls;
using BillPro.Models;
using BillPro.Repositories;
using BillPro.Helpers; // Added for FormValidator

namespace BillPro.Views
{
    public partial class ItemCategoryRegistrationView : UserControl
    {
        private readonly ItemCategoryRepository _repository;
        private const string CurrentUserId = "ADMIN";

        public ItemCategoryRegistrationView()
        {
            InitializeComponent();
            _repository = new ItemCategoryRepository();
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
                // 1. Updated to the Generic Repository Method
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
                // 2. Replaced manual checks with Form Validator
                if (!FormValidator.ValidateRequiredFields((txtCategoryName, "Category Name")))
                    return;

                string catName = txtCategoryName.Text.Trim();

                if (_repository.CategoryExists(catName))
                {
                    MessageBox.Show("This category is already existing!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    txtCategoryName.Focus();
                    return;
                }

                var newCategory = new ItemCategory
                {
                    ItemCategoryCode = _repository.GetNextCategoryCode(),
                    ItemCategoryName = catName,
                    CreatedBy = CurrentUserId,
                    CreatedDate = DateTime.Now.ToString("s")
                };

                _repository.AddCategory(newCategory);
                txtCategoryName.Clear();
                LoadCategories();
                txtCategoryName.Focus();
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
                if (lstCategories.SelectedItem is ItemCategory selected)
                {
                    if (selected.ItemCategoryCode <= 100)
                    {
                        MessageBox.Show("Only Programmers have the right to edit this Item Category!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    // 2. Replaced manual checks with Form Validator
                    if (!FormValidator.ValidateRequiredFields((txtCategoryName, "Category Name")))
                        return;

                    selected.ItemCategoryName = txtCategoryName.Text.Trim();
                    selected.LastModifiedBy = CurrentUserId;
                    selected.LastModifiedDate = DateTime.Now.ToString("s");

                    _repository.UpdateCategory(selected);
                    txtCategoryName.Clear();
                    LoadCategories();
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
                if (lstCategories.SelectedItem is ItemCategory selected)
                {
                    if (selected.ItemCategoryCode <= 100)
                    {
                        MessageBox.Show("Only Programmers have the right to delete this Item Category!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (_repository.IsCategoryInUse(selected.ItemCategoryCode))
                    {
                        MessageBox.Show("Can't Delete this Item Category!\n\nCategory already used in Item Master", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (MessageBox.Show("The selected item will be permanently deleted!\n\nAre you sure?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        // 3. Updated to the Generic Repository Method requiring the Primary Key column name
                        _repository.Delete("item_category_code", selected.ItemCategoryCode);

                        txtCategoryName.Clear();
                        LoadCategories();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Deleting Category:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

       private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstCategories.SelectedItem is ItemCategory selected)
            {
                txtCategoryName.Text = selected.ItemCategoryName;
            }
        }
    }
}