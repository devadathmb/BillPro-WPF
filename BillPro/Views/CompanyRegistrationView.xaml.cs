using System;
using System.Windows;
using System.Windows.Controls;
using BillPro.Models;
using BillPro.Repositories;
using BillPro.Helpers; 

namespace BillPro.Views
{
    public partial class CompanyRegistrationView : UserControl
    {
        private readonly CompanyMasterRepository _companyRepo;
        private const string CurrentUserId = "ADMIN";

        public CompanyRegistrationView()
        {
            InitializeComponent();
            _companyRepo = new CompanyMasterRepository();
            _companyRepo.InitializeDatabase();
            LoadCompanies();
            this.Loaded += (sender, args) =>
            {
                Dispatcher.BeginInvoke(new Action(() => txtCompanyName?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            };
        }

        private void LoadCompanies()
        {
            try
            {
                lstCompanies.ItemsSource = _companyRepo.GetAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Loading Companies:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. ONE-LINE VALIDATION! 
                // This automatically checks if it's empty, shows the error, and focuses the textbox if it fails.
                if (!FormValidator.ValidateRequiredFields((txtCompanyName, "Company Name")))
                    return;

                string companyName = txtCompanyName.Text.Trim();

                // 2. Check for duplicates
                if (_companyRepo.CompanyExists(companyName))
                {
                    MessageBox.Show("This company already exists!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    txtCompanyName.Focus();
                    return;
                }

                var newCompany = new CompanyMaster
                {
                    CompanyCode = _companyRepo.GetNextCompanyCode(),
                    CompanyName = companyName,
                    CreatedBy = CurrentUserId,
                    CreatedDate = DateTime.Now.ToString("s")
                };

                _companyRepo.AddCompany(newCompany);
                txtCompanyName.Clear();
                LoadCompanies();
                txtCompanyName.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Adding Company:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireEdit()) return;
            try
            {
                if (lstCompanies.SelectedItem is CompanyMaster selected)
                {
                    // 1. ONE-LINE VALIDATION!
                    if (!FormValidator.ValidateRequiredFields((txtCompanyName, "Company Name")))
                        return;

                    selected.CompanyName = txtCompanyName.Text.Trim();
                    selected.LastModifiedBy = CurrentUserId;
                    selected.LastModifiedDate = DateTime.Now.ToString("s");

                    _companyRepo.UpdateCompany(selected);
                    txtCompanyName.Clear();
                    LoadCompanies();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Editing Company:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            try
            {
                if (lstCompanies.SelectedItem is CompanyMaster selected)
                {
                    if (_companyRepo.IsCompanyInUse(selected.CompanyCode))
                    {
                        MessageBox.Show("Can't Delete this Company!\n\nCompany is already used in Item Master.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (MessageBox.Show("The selected company will be permanently deleted!\n\nAre you sure?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        // 3. GENERIC REPO FIX!
                        // Remember that our new GenericRepository Delete method requires the column name AND the value!
                        _companyRepo.Delete("company_code", selected.CompanyCode);

                        txtCompanyName.Clear();
                        LoadCompanies();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Deleting Company:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LstCompanies_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstCompanies.SelectedItem is CompanyMaster selected)
            {
                txtCompanyName.Text = selected.CompanyName;
            }
        }
    }
}