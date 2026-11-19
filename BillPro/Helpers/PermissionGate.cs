using System.Windows;

namespace BillPro.Helpers
{
    /// <summary>
    /// Single choke point for the edit/delete permission check that needs
    /// to land in every entry view's Save/Update/Cancel handler (~30 views).
    /// Usage, at the top of BtnSave_Click when in Update mode, and at the
    /// top of BtnCancelBill_Click / any delete handler:
    ///
    ///     if (!PermissionGate.RequireEdit()) return;
    ///     if (!PermissionGate.RequireDelete()) return;
    ///
    /// Admin and Developer always pass both checks. Normal users pass
    /// based on their own CanEdit/CanDelete flags, set at account creation
    /// (User Registration screen) and adjustable later from the same
    /// screen.
    /// </summary>
    public static class PermissionGate
    {
        public static bool CanEdit =>
            UserSession.Current != null && (UserSession.Current.IsAdminOrAbove || UserSession.Current.CanEdit);

        public static bool CanDelete =>
            UserSession.Current != null && (UserSession.Current.IsAdminOrAbove || UserSession.Current.CanDelete);

        public static bool RequireEdit()
        {
            if (CanEdit) return true;
            MessageBox.Show("You don't have permission to edit or update entries.",
                "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        public static bool RequireDelete()
        {
            if (CanDelete) return true;
            MessageBox.Show("You don't have permission to delete or cancel entries.",
                "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}