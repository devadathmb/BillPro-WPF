using System;
using System.Windows;
using System.Windows.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class UserRegistrationView : UserControl
    {
        private readonly UserRepository _userRepo = new();

        public UserRegistrationView()
        {
            InitializeComponent();
            Loaded += (s, e) => Initialize();
        }

        private void Initialize()
        {
            _userRepo.InitializeDatabase();

            // Developer can create Admin or Normal users. Admin can only
            // create Normal users — there is meant to be exactly one
            // Developer account, seeded once at first run, never created
            // from this screen by anyone.
            var availableRoles = UserSession.Current?.IsDeveloper == true
                ? new[] { UserRole.Admin, UserRole.Normal }
                : new[] { UserRole.Normal };

            cmbRole.ItemsSource = availableRoles;
            cmbRole.SelectedIndex = 0;

            LoadUsers();
        }

        private void LoadUsers()
        {
            dgUsers.ItemsSource = _userRepo.GetManageableUsers();
        }

        private void CmbRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isNormal = cmbRole.SelectedItem is UserRole.Normal;
            pnlNormalPermissions.Visibility = isNormal ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            txtCreateError.Visibility = Visibility.Collapsed;
            txtCreateSuccess.Visibility = Visibility.Collapsed;

            string username = txtUsername.Text.Trim();
            string displayName = txtDisplayName.Text.Trim();
            string password = pwdPassword.Password;
            string confirm = pwdConfirm.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(displayName))
            {
                ShowError("Username and display name are required.");
                return;
            }

            if (password != confirm)
            {
                ShowError("Password and confirmation do not match.");
                return;
            }

            if (cmbRole.SelectedItem is not UserRole role)
            {
                ShowError("Please select a role.");
                return;
            }

            // Admin/Developer accounts always have full edit/delete power —
            // the checkboxes only apply to Normal users, and are hidden for
            // the other roles (see CmbRole_SelectionChanged).
            bool canEdit = role != UserRole.Normal || chkCanEdit.IsChecked == true;
            bool canDelete = role != UserRole.Normal || chkCanDelete.IsChecked == true;

            try
            {
                _userRepo.CreateUser(username, displayName, password, role, canEdit, canDelete);

                txtUsername.Clear();
                txtDisplayName.Clear();
                pwdPassword.Clear();
                pwdConfirm.Clear();
                chkCanEdit.IsChecked = false;
                chkCanDelete.IsChecked = false;

                txtCreateSuccess.Text = $"User '{username}' created successfully.";
                txtCreateSuccess.Visibility = Visibility.Visible;

                LoadUsers();
            }
            catch (Exception ex)
            {
                // Most likely cause: UNIQUE constraint on username.
                ShowError("Could not create user: " + ex.Message);
            }
        }

        /// <summary>Handles clicks on the Can Edit / Can Delete / Active
        /// checkboxes in the EXISTING USERS grid. By the time Click fires,
        /// the two-way binding has already pushed the new value into the
        /// bound UserListItem, so user.can_edit / can_delete / is_active
        /// already reflect the new state — this just persists that state
        /// to the database. Admin rows have these checkboxes disabled via
        /// the XAML style triggers, so this only ever fires for Normal
        /// users in practice (Developer is never listed here at all).</summary>
        private void ChkPermission_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox chk || chk.DataContext is not UserListItem user) return;

            bool isChecked = chk.IsChecked == true;

            try
            {
                switch (chk.Tag as string)
                {
                    case "CanEdit":
                        user.can_edit = isChecked ? 1 : 0;
                        _userRepo.UpdatePermissions(user.id, user.can_edit != 0, user.can_delete != 0);
                        break;

                    case "CanDelete":
                        user.can_delete = isChecked ? 1 : 0;
                        _userRepo.UpdatePermissions(user.id, user.can_edit != 0, user.can_delete != 0);
                        break;

                    case "IsActive":
                        user.is_active = isChecked ? 1 : 0;
                        _userRepo.SetActive(user.id, isChecked);
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update user: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                LoadUsers(); // reload from DB so the UI can't show a state that didn't actually save
            }
        }

        private void BtnDeleteUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not UserListItem user) return;

            var result = MessageBox.Show(
                $"Delete the account '{user.username}' ({user.display_name})?\n\n" +
                "This only removes their login and permissions — it does not touch any " +
                "bills, entries, or reports they created; those keep their username as a " +
                "plain historical record. This cannot be undone.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            _userRepo.DeleteUser(user.id);
            LoadUsers();
        }

        private void ShowError(string message)
        {
            txtCreateError.Text = message;
            txtCreateError.Visibility = Visibility.Visible;
        }
    }
}