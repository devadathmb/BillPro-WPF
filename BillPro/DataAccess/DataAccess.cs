using System;
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;

namespace BillPro.DataAccess
{
    public static class DatabaseConfig
    {
        // DEBUG builds: the database folder is anchored to the solution
        // folder (wherever BillPro.sln lives on disk), not the build output
        // folder. Exe-relative paths (AppDomain.CurrentDomain.BaseDirectory)
        // change every time the build output folder changes — Debug vs
        // Release, AnyCPU vs x64 vs ARM64 — which meant each combination
        // silently got its own empty database. Anchoring to the solution
        // folder gives one fixed location regardless of build
        // configuration/platform while developing.
        //
        // RELEASE builds: there is no .sln on a client machine, so instead
        // of searching for one, the data folder is anchored directly to the
        // folder the .exe itself is running from — i.e. wherever the app
        // was installed/copied to. This is decided once at compile time via
        // the #if DEBUG / #else below, not detected at runtime, so a
        // Release build never even attempts the upward directory search.
        private static string GetSmartDataFolder()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

#if DEBUG
            string solutionDir = FindSolutionDirectory(baseDir);

            // Fallback: if no .sln is found (e.g. Debug bits run from
            // somewhere unusual), fall back to the exe-relative behavior
            // rather than crashing.
            string rootDir = solutionDir ?? baseDir;
#else
            // Release: always the exe's own folder, no search.
            string rootDir = baseDir;
#endif

            string dataFolder = Path.Combine(rootDir, "data");
            if (!Directory.Exists(dataFolder))
            {
                Directory.CreateDirectory(dataFolder);
            }
            return dataFolder;
        }

        // Walks upward from the given start directory until it finds a
        // folder containing a .sln file, or hits the filesystem root.
        // Only ever called from the DEBUG branch above.
        private static string FindSolutionDirectory(string startDir)
        {
            var dir = new DirectoryInfo(startDir);

            while (dir != null)
            {
                if (dir.GetFiles("*.sln").Length > 0)
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            return null;
        }

        // Run the smart check once
        public static readonly string DataFolder = GetSmartDataFolder();

        // ==========================================
        // INVENTORY DATABASE
        // ==========================================
        // Notice we are using DataFolder now instead of AppFolder!
        public static readonly string DbPath = Path.Combine(DataFolder, "inventory.db");

        // "Default Timeout" (seconds) maps straight onto SQLite's
        // PRAGMA busy_timeout — without it, any connection that hits a lock
        // held by another connection throws SQLITE_BUSY ("database is
        // locked") immediately instead of waiting for it to clear.
        public static string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString();

        public static IDbConnection GetConnection() => OpenWithPragmas(ConnectionString);

        // ==========================================
        // ACCOUNTS DATABASE
        // ==========================================
        public static readonly string AccountsDbPath = Path.Combine(DataFolder, "accounts.db");

        public static string AccountsConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = AccountsDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString();

        public static IDbConnection GetAccountsConnection() => OpenWithPragmas(AccountsConnectionString);

        // Opens the connection eagerly (rather than leaving it for Dapper to
        // lazily open) so WAL mode can be enabled before any query runs.
        // WAL lets readers keep working while one connection writes, instead
        // of every writer taking an exclusive lock on the whole file — the
        // other half of fixing "database is locked" alongside busy_timeout.
        private static SqliteConnection OpenWithPragmas(string connectionString)
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode = WAL;";
                cmd.ExecuteNonQuery();
            }

            return conn;
        }
    }
}
