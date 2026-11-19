using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    /// <summary>
    /// Username/password + role/permission store for the login screen and
    /// the User Registration / Menu Customization / Change Password admin
    /// screens. Lives in inventory.db alongside everything else, via the
    /// same DatabaseConfig (WAL mode + busy_timeout) every other repository
    /// in the app uses.
    ///
    /// Both username and password are case-sensitive: username lookups use
    /// an exact (BINARY-collation) match — no COLLATE NOCASE — and password
    /// verification is already case-sensitive by construction, since it's
    /// an exact string compare against a SHA-256 hash. "Admin" and "admin"
    /// are therefore two different, unrelated usernames as far as the DB's
    /// UNIQUE constraint and every lookup here are concerned.
    ///
    /// Passwords are stored as salted SHA-256 hashes — adequate for a
    /// single-machine retail app with a handful of staff logins. If this
    /// ever needs to be internet-facing, switch to BCrypt/Argon2 instead.
    ///
    /// Note on PasswordHelper: BillPro.Helpers.PasswordHelper (the unsalted
    /// SHA-256 hex hash previously used to gate the Company Details hidden
    /// shortcut) is intentionally NOT reused here — this repository's own
    /// per-user salted hashing is stronger, and mixing the two schemes for
    /// one shared "password" concept would be more confusing than useful.
    /// PasswordHelper is now unused (Company Details is gated by
    /// UserSession.Current.IsDeveloper instead) and can be deleted once
    /// you're comfortable nothing else references it.
    /// </summary>
    public class UserRepository
    {
        public void InitializeDatabase()
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            db.Execute(@"
                CREATE TABLE IF NOT EXISTS app_users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT NOT NULL UNIQUE,
                    display_name TEXT NOT NULL,
                    password_hash TEXT NOT NULL,
                    password_salt TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );");

            // Additive self-heal, same pattern as every other repository's
            // InitializeDatabase — safe to run on every startup, a no-op
            // once the columns exist.
            AddColumnIfMissing(db, "app_users", "role", "TEXT NOT NULL DEFAULT 'Normal'");
            AddColumnIfMissing(db, "app_users", "can_edit", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "app_users", "can_delete", "INTEGER NOT NULL DEFAULT 0");

            db.Execute(@"
                CREATE TABLE IF NOT EXISTS user_menu_denials (
                    user_id INTEGER NOT NULL,
                    form_key TEXT NOT NULL,
                    PRIMARY KEY (user_id, form_key)
                );");

            // Reserved-account seed — the original default login (now under
            // the Admin role) and the Developer account. Checked and created
            // INDEPENDENTLY by username rather than gated on the whole table
            // being empty (the old "if (count == 0)" check): that meant if
            // the Developer row was ever deleted, or only one of the two
            // accounts existed because a first run was interrupted between
            // the two CreateUser calls, neither would ever be recreated on
            // a later startup since app_users was no longer empty. This now
            // runs on every InitializeDatabase() call (i.e. every startup)
            // and is a no-op once both accounts already exist.
            EnsureUserExists(db, ReservedDefaults.DefaultAdminUsername,
                ReservedDefaults.DefaultAdminDisplayName,
                ReservedDefaults.DefaultAdminPassword,
                UserRole.Admin, canEdit: true, canDelete: true);

            EnsureUserExists(db, ReservedDefaults.DeveloperUsername,
                ReservedDefaults.DeveloperDisplayName,
                ReservedDefaults.DeveloperPassword,
                UserRole.Developer, canEdit: true, canDelete: true);
        }

        /// <summary>Creates the given reserved account only if a user with
        /// that EXACT (case-sensitive) username doesn't already exist.
        /// Called on every startup for both the Admin and Developer default
        /// accounts so either one gets silently recreated if it's ever
        /// missing, without duplicating or resetting an account that's
        /// already there.</summary>
        private void EnsureUserExists(IDbConnection db, string username, string displayName, string plainPassword,
            UserRole role, bool canEdit, bool canDelete)
        {
            long exists = db.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM app_users WHERE username = @u",
                new { u = username });

            if (exists == 0)
                CreateUser(db, username, displayName, plainPassword, role, canEdit, canDelete);
        }

        private static void AddColumnIfMissing(IDbConnection db, string table, string column, string definition)
        {
            var columns = db.Query($"PRAGMA table_info({table});")
                .Select(r => (string)r.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!columns.Contains(column))
                db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        }

        public LoginResult TryLogin(string username, string password)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();

            var row = db.QueryFirstOrDefault<UserRow>(
                @"SELECT id, username, display_name, password_hash, password_salt, is_active, role, can_edit, can_delete
                  FROM app_users WHERE username = @u",
                new { u = username?.Trim() });

            if (row == null || row.is_active == 0) return LoginResult.Failed;

            string computedHash = Hash(password ?? "", row.password_salt);
            if (computedHash != row.password_hash) return LoginResult.Failed;

            var deniedFormKeys = db.Query<string>(
                "SELECT form_key FROM user_menu_denials WHERE user_id = @id",
                new { id = row.id }).ToList();

            var role = Enum.TryParse<UserRole>(row.role, out var parsedRole) ? parsedRole : UserRole.Normal;

            return new LoginResult
            {
                Success = true,
                UserId = row.id,
                DisplayName = row.display_name,
                Role = role,
                CanEdit = row.can_edit != 0,
                CanDelete = row.can_delete != 0,
                DeniedFormKeys = deniedFormKeys
            };
        }

        public void CreateUser(string username, string displayName, string plainPassword,
            UserRole role, bool canEdit, bool canDelete)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            CreateUser(db, username, displayName, plainPassword, role, canEdit, canDelete);
        }

        public void CreateUser(IDbConnection db, string username, string displayName, string plainPassword,
            UserRole role, bool canEdit, bool canDelete)
        {
            string salt = NewSalt();
            string hash = Hash(plainPassword, salt);

            db.Execute(
                @"INSERT INTO app_users (username, display_name, password_hash, password_salt, role, can_edit, can_delete)
                  VALUES (@u, @d, @h, @s, @role, @canEdit, @canDelete)",
                new
                {
                    u = username,
                    d = displayName,
                    h = hash,
                    s = salt,
                    role = role.ToString(),
                    canEdit = canEdit ? 1 : 0,
                    canDelete = canDelete ? 1 : 0
                });
        }

        /// <summary>Admin + Normal users only — the Developer account is
        /// never listed here. There is meant to be exactly one, created
        /// solely via the first-run seed above.</summary>
        public List<UserListItem> GetManageableUsers()
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            return db.Query<UserListItem>(
                @"SELECT id, username, display_name, role, can_edit, can_delete, is_active
                  FROM app_users WHERE role <> 'Developer' ORDER BY display_name").ToList();
        }

        /// <summary>Just Normal users — for the Menu Customization dropdown,
        /// since only Normal users have per-form menu denials at all.</summary>
        public List<UserListItem> GetNormalUsers()
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            return db.Query<UserListItem>(
                @"SELECT id, username, display_name, role, can_edit, can_delete, is_active
                  FROM app_users WHERE role = 'Normal' ORDER BY display_name").ToList();
        }

        public void SetActive(int userId, bool isActive)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            db.Execute("UPDATE app_users SET is_active = @a WHERE id = @id",
                new { a = isActive ? 1 : 0, id = userId });
        }

        /// <summary>Hard-deletes the account and its menu denial rows.
        /// Safe with respect to historical data — every repository's
        /// currentUser is stored as a plain username snapshot on each bill/
        /// entry, not a foreign key into app_users, so deleting an account
        /// here never touches past records.</summary>
        public void DeleteUser(int userId)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();

            string role = db.ExecuteScalar<string>("SELECT role FROM app_users WHERE id = @id", new { id = userId });
            if (role == UserRole.Developer.ToString())
                throw new InvalidOperationException("The Developer account cannot be deleted.");

            using var tx = db.BeginTransaction();

            db.Execute("DELETE FROM user_menu_denials WHERE user_id = @id", new { id = userId }, tx);
            db.Execute("DELETE FROM app_users WHERE id = @id", new { id = userId }, tx);

            tx.Commit();
        }

        public void UpdatePermissions(int userId, bool canEdit, bool canDelete)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            db.Execute("UPDATE app_users SET can_edit = @e, can_delete = @d WHERE id = @id",
                new { e = canEdit ? 1 : 0, d = canDelete ? 1 : 0, id = userId });
        }

        public (bool Success, string Error) ChangePassword(string username, string currentPassword, string newPassword,
            bool callerIsDeveloper)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();

            var row = db.QueryFirstOrDefault<UserRow>(
                "SELECT id, role, password_hash, password_salt FROM app_users WHERE username = @u",
                new { u = username?.Trim() });

            if (row == null) return (false, "No user with that username.");

            if (row.role == UserRole.Developer.ToString() && !callerIsDeveloper)
                return (false, "The Developer account's password can only be changed by the Developer.");

            if (Hash(currentPassword ?? "", row.password_salt) != row.password_hash)
                return (false, "Current password is incorrect.");

            string newSalt = NewSalt();
            string newHash = Hash(newPassword, newSalt);

            db.Execute("UPDATE app_users SET password_hash = @h, password_salt = @s WHERE id = @id",
                new { h = newHash, s = newSalt, id = row.id });

            return (true, null);
        }

        public HashSet<string> GetDeniedFormKeys(int userId)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            return db.Query<string>("SELECT form_key FROM user_menu_denials WHERE user_id = @id",
                new { id = userId }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Replaces the full denial set for a user in one
        /// transaction — simplest correct way to persist a checkbox tree
        /// where any node's state may have changed.</summary>
        public void SetDeniedFormKeys(int userId, IEnumerable<string> formKeys)
        {
            using IDbConnection db = DatabaseConfig.GetConnection();
            using var tx = db.BeginTransaction();

            db.Execute("DELETE FROM user_menu_denials WHERE user_id = @id", new { id = userId }, tx);

            foreach (var key in formKeys.Distinct())
            {
                db.Execute("INSERT INTO user_menu_denials (user_id, form_key) VALUES (@id, @key)",
                    new { id = userId, key }, tx);
            }

            tx.Commit();
        }

        private static string NewSalt()
        {
            byte[] saltBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(saltBytes);
            return Convert.ToBase64String(saltBytes);
        }

        private static string Hash(string password, string salt)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + password));
            return Convert.ToBase64String(bytes);
        }

        private class UserRow
        {
            public int id { get; set; }
            public string username { get; set; }
            public string display_name { get; set; }
            public string password_hash { get; set; }
            public string password_salt { get; set; }
            public int is_active { get; set; }
            public string role { get; set; }
            public int can_edit { get; set; }
            public int can_delete { get; set; }
        }
    }

    public class LoginResult
    {
        public bool Success { get; set; }
        public int UserId { get; set; }
        public string DisplayName { get; set; }
        public UserRole Role { get; set; }
        public bool CanEdit { get; set; }
        public bool CanDelete { get; set; }
        public List<string> DeniedFormKeys { get; set; } = new();

        public static LoginResult Failed => new LoginResult { Success = false };
    }

    public class UserListItem
    {
        public int id { get; set; }
        public string username { get; set; }
        public string display_name { get; set; }
        public string role { get; set; }
        public int can_edit { get; set; }
        public int can_delete { get; set; }
        public int is_active { get; set; }
    }
}