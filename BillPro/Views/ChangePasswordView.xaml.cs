using System.Windows;
using System.Windows.Controls;
using BillPro.Helpers;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class ChangePasswordView : UserControl
    {
        private readonly UserRepository _userRepo = new();

        public ChangePasswordView()
        {
            InitializeComponent();
        }

        private void BtnChange_Click(object sender, RoutedEventArgs e)
        {
            
            txtError.Visibility = Visibility.Collapsed;
            txtSuccess.Visibility = Visibility.Collapsed;

            string username = txtUsername.Text.Trim();
            string current = pwdCurrent.Password;
            string newPass = pwdNew.Password;
            string confirm = pwdConfirm.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(current) || string.IsNullOrEmpty(newPass))
            {
                ShowError("All fields are required.");
                return;
            }

            if (current == newPass)
            {
                ShowError("The New Password cannot be the same as the Old Password");
                return;
            }


            if (newPass != confirm)
            {
                ShowError("New password and confirmation do not match.");
                return;
            }

            var (success, error) = _userRepo.ChangePassword(
            username, current, newPass, callerIsDeveloper: UserSession.Current?.IsDeveloper == true);


            if (!success)
            {
                ShowError(error);
                return;
            }

            txtSuccess.Text = txtUsername.Text + "'s Password changed successfully.";
            txtSuccess.Visibility = Visibility.Visible;

            txtUsername.Clear();
            pwdCurrent.Clear();
            pwdNew.Clear();
            pwdConfirm.Clear();
        }

        private void ShowError(string message)
        {
            txtError.Text = message;
            txtError.Visibility = Visibility.Visible;
        }
    }
}