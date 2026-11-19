using System;
using System.Windows;
using System.Windows.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class VehicleRegistrationView : UserControl
    {
        // 1. SPECIFIC NAMING CONVENTION!
        private readonly VehicleRepository _vehicleRepo;
        private const string CurrentUserId = "ADMIN";

        public VehicleRegistrationView()
        {
            InitializeComponent();
            _vehicleRepo = new VehicleRepository();
            _vehicleRepo.InitializeDatabase();
            LoadVehicles();

            this.Loaded += (sender, args) =>
            {
                Dispatcher.BeginInvoke(new Action(() => txtVehicleNumber?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            };
        }

        private void LoadVehicles()
        {
            try
            {
                // 2. GENERIC REPO UPDATE
                lstVehicles.ItemsSource = _vehicleRepo.GetAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Loading Vehicles:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!FormValidator.ValidateRequiredFields((txtVehicleNumber, "Vehicle Number")))
                    return;

                string vehicleNumber = txtVehicleNumber.Text.Trim();

                if (_vehicleRepo.VehicleExists(vehicleNumber))
                {
                    MessageBox.Show("This vehicle number already exists!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    txtVehicleNumber.Focus();
                    return;
                }

                var newVehicle = new VehicleMaster
                {
                    VehicleCode = _vehicleRepo.GetNextVehicleCode(),
                    VehicleNumber = vehicleNumber,
                    CreatedBy = CurrentUserId,
                    CreatedDate = DateTime.Now.ToString("s")
                };

                _vehicleRepo.AddVehicle(newVehicle);
                txtVehicleNumber.Clear();
                LoadVehicles();
                txtVehicleNumber.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Adding Vehicle:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireEdit()) return;
            try
            {
                if (lstVehicles.SelectedItem is VehicleMaster selected)
                {
                    // Form Validator added here too!
                    if (!FormValidator.ValidateRequiredFields((txtVehicleNumber, "Vehicle Number")))
                        return;

                    selected.VehicleNumber = txtVehicleNumber.Text.Trim();
                    selected.LastModifiedBy = CurrentUserId;
                    selected.LastModifiedDate = DateTime.Now.ToString("s");

                    _vehicleRepo.UpdateVehicle(selected);
                    txtVehicleNumber.Clear();
                    LoadVehicles();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Editing Vehicle:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            try
            {
                if (lstVehicles.SelectedItem is VehicleMaster selected)
                {
                    if (_vehicleRepo.IsVehicleInUse(selected.VehicleCode))
                    {
                        MessageBox.Show("Can't Delete this Vehicle!\n\nVehicle is already associated with transactions.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (MessageBox.Show("The selected vehicle will be permanently deleted!\n\nAre you sure?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        // 3. GENERIC REPO UPDATE
                        _vehicleRepo.Delete("VehicleCode", selected.VehicleCode);

                        txtVehicleNumber.Clear();
                        LoadVehicles();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error Deleting Vehicle:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

   private void LstVehicles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstVehicles.SelectedItem is VehicleMaster selected)
            {
                txtVehicleNumber.Text = selected.VehicleNumber;
            }
        }
    }
}