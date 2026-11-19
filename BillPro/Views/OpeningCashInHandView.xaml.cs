using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Lets the user enter/update the one-time opening cash-in-hand balance
    // that TaxLedgerRepository.GetBalanceBefore anchors the Day Book's
    // running "Balance b/f" figure to. Single row under the hood
    // (opening_cash_balance, id = 1) — this screen always shows/replaces
    // that one row, never creates a history of them.
    public partial class OpeningCashInHandView : UserControl
    {
        private readonly TaxLedgerRepository _taxLedgerRepo;

        public OpeningCashInHandView()
        {
            InitializeComponent();

            _taxLedgerRepo = new TaxLedgerRepository();

            LoadCurrent();
        }

        private void LoadCurrent()
        {
            var opening = _taxLedgerRepo.GetOpeningBalance();

            if (string.IsNullOrEmpty(opening.UpdatedDate))
            {
                lblCurrentBalance.Text = "Not set";
                lblCurrentBalance.Foreground = System.Windows.Media.Brushes.Gray;
                lblCurrentDetails.Text = "No opening balance has been configured yet — the Day Book currently starts from zero.";

                txtAmount.Text = "0";
                return;
            }

            lblCurrentBalance.Text = opening.Amount.ToString("N2", CultureInfo.InvariantCulture);
            lblCurrentBalance.Foreground = opening.Amount < 0
                ? System.Windows.Media.Brushes.Firebrick
                : System.Windows.Media.Brushes.Black;

            lblCurrentDetails.Text = string.IsNullOrEmpty(opening.UpdatedBy)
                ? $"Last updated {opening.UpdatedDate}"
                : $"Last updated by {opening.UpdatedBy} on {opening.UpdatedDate}";

            txtAmount.Text = opening.Amount.ToString(CultureInfo.InvariantCulture);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (!double.TryParse(txtAmount.Text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out double amount))
            {
                MessageBox.Show("Enter a valid amount (negative is fine, e.g. -1500).",
                    "Invalid Amount", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Set the opening cash-in-hand balance to {amount:N2}?\n\n" +
                "This replaces any previously stored opening balance and changes every Day Book " +
                "'Balance b/f' figure across the whole report.",
                "Confirm Opening Balance", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            btnSave.IsEnabled = false;
            try
            {
                // TODO: swap in your actual logged-in username source if the
                // app tracks one, same as ReSyncSettingsView.
                string currentUser = Environment.UserName;

                _taxLedgerRepo.SetOpeningBalance(amount, currentUser);

                lblStatus.Text = "Saved.";
                LoadCurrent();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Failed — see error.";
                MessageBox.Show("Error saving opening balance:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnSave.IsEnabled = true;
            }
        }
    }
}