using System;
using System.Collections.Generic;
using BillPro.Models;

namespace BillPro.Helpers
{
    /// <summary>
    /// Who's logged in, for the lifetime of this app run. Created once in
    /// LoginWindow right after a successful TryLogin, before MainWindow is
    /// constructed — see LoginWindow.xaml.cs. Every permission check in the
    /// app (PermissionGate, MainWindow's nav filtering, the Company Details
    /// gate) reads off UserSession.Current rather than re-querying the DB.
    /// </summary>
    public class UserSession
    {
        public int UserId { get; }
        public string Username { get; }
        public string DisplayName { get; }
        public UserRole Role { get; }
        public bool CanEdit { get; }
        public bool CanDelete { get; }

        private readonly HashSet<string> _deniedFormKeys;

        private UserSession(int userId, string username, string displayName, UserRole role,
            bool canEdit, bool canDelete, IEnumerable<string> deniedFormKeys)
        {
            UserId = userId;
            Username = username;
            DisplayName = displayName;
            Role = role;
            CanEdit = canEdit;
            CanDelete = canDelete;
            _deniedFormKeys = new HashSet<string>(deniedFormKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static UserSession Current { get; private set; }

        public bool IsDeveloper => Role == UserRole.Developer;
        public bool IsAdminOrAbove => Role == UserRole.Admin || Role == UserRole.Developer;

        /// <summary>formKey is the same string used as the nav title in
        /// MainWindow.BuildNavTree's Leaf(...) calls — reused as-is rather
        /// than inventing a separate key scheme.</summary>
        public bool CanSeeForm(string formKey) => !_deniedFormKeys.Contains(formKey);

        public static void Start(int userId, string username, string displayName, UserRole role,
            bool canEdit, bool canDelete, IEnumerable<string> deniedFormKeys)
        {
            Current = new UserSession(userId, username, displayName, role, canEdit, canDelete, deniedFormKeys);
        }

        public static void End() => Current = null;
    }
}