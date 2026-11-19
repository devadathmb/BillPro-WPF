using Dapper;
using System.Data;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // ─────────────────────────────────────────────────────────────────────
    // Single-row settings table — same InitializeDatabase/guard pattern as
    // SalesRepository/SalesSeriesRepository, just with one fixed row (id=1)
    // instead of a growing set. Get() returns a CompanyDetails with empty
    // strings (never null) if nothing has been saved yet, so the print
    // helper and the config form never have to null-check every field.
    // ─────────────────────────────────────────────────────────────────────
    public class CompanyRepository
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new();

        public void InitializeDatabase()
        {
            lock (_initLock)
            {
                if (_initialized) return;

                using (IDbConnection db = DatabaseConfig.GetConnection())
                {
                    db.Execute(@"
                    CREATE TABLE IF NOT EXISTS company_details (
                        id                   INTEGER PRIMARY KEY,
                        company_name         TEXT,
                        reg_number           TEXT,
                        address              TEXT,
                        phone1_label         TEXT,
                        phone1               TEXT,
                        phone2_label         TEXT,
                        phone2               TEXT,
                        email                TEXT,
                        logo_path            TEXT,
                        bank1_name           TEXT,
                        bank1_account_no     TEXT,
                        bank1_account_holder TEXT,
                        bank2_name           TEXT,
                        bank2_account_no     TEXT,
                        bank2_account_holder TEXT,
                        is_locked            INTEGER NOT NULL DEFAULT 0
                    );");

                    // Self-heal for DBs created before is_locked existed —
                    // same pattern used elsewhere (AddColumnIfMissing).
                    AddColumnIfMissing(db, "is_locked", "INTEGER NOT NULL DEFAULT 0");
                    AddColumnIfMissing(db, "logo_path", "TEXT");
                }

                _initialized = true;
            }
        }

        private void AddColumnIfMissing(IDbConnection db, string columnName, string columnDef)
        {
            try
            {
                db.Execute($"ALTER TABLE company_details ADD COLUMN {columnName} {columnDef};");
            }
            catch (System.Exception)
            {
                // Column already exists — SQLite has no IF NOT EXISTS for
                // ALTER TABLE ADD COLUMN, so the exception is the check.
            }
        }

        /// <summary>Loads the single company-details row, or an all-blank default if it hasn't been set up yet.</summary>
        public CompanyDetails Get()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var row = db.QueryFirstOrDefault<CompanyDetails>(
                    @"SELECT id AS Id, company_name AS CompanyName, reg_number AS RegNumber,
                             address AS Address,
                             phone1_label AS Phone1Label, phone1 AS Phone1,
                             phone2_label AS Phone2Label, phone2 AS Phone2,
                             email AS Email,
                             logo_path AS LogoPath,
                             bank1_name AS Bank1Name, bank1_account_no AS Bank1AccountNo, bank1_account_holder AS Bank1AccountHolder,
                             bank2_name AS Bank2Name, bank2_account_no AS Bank2AccountNo, bank2_account_holder AS Bank2AccountHolder,
                             is_locked AS IsLocked
                      FROM company_details WHERE id = 1");

                return row ?? new CompanyDetails
                {
                    Id = 1,
                    CompanyName = "",
                    RegNumber = "",
                    Address = "",
                    Phone1Label = "",
                    Phone1 = "",
                    Phone2Label = "",
                    Phone2 = "",
                    Email = "",
                    LogoPath = "",
                    Bank1Name = "",
                    Bank1AccountNo = "",
                    Bank1AccountHolder = "",
                    Bank2Name = "",
                    Bank2AccountNo = "",
                    Bank2AccountHolder = "",
                    IsLocked = false
                };
            }
        }

        /// <summary>Upserts the single settings row (id fixed at 1).</summary>
        public void Save(CompanyDetails d)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute(@"
                    INSERT INTO company_details
                        (id, company_name, reg_number, address,
                         phone1_label, phone1, phone2_label, phone2, email,
                         logo_path,
                         bank1_name, bank1_account_no, bank1_account_holder,
                         bank2_name, bank2_account_no, bank2_account_holder,
                         is_locked)
                    VALUES
                        (1, @CompanyName, @RegNumber, @Address,
                         @Phone1Label, @Phone1, @Phone2Label, @Phone2, @Email,
                         @LogoPath,
                         @Bank1Name, @Bank1AccountNo, @Bank1AccountHolder,
                         @Bank2Name, @Bank2AccountNo, @Bank2AccountHolder,
                         @IsLocked)
                    ON CONFLICT(id) DO UPDATE SET
                        company_name = @CompanyName,
                        reg_number = @RegNumber,
                        address = @Address,
                        phone1_label = @Phone1Label,
                        phone1 = @Phone1,
                        phone2_label = @Phone2Label,
                        phone2 = @Phone2,
                        email = @Email,
                        logo_path = @LogoPath,
                        bank1_name = @Bank1Name,
                        bank1_account_no = @Bank1AccountNo,
                        bank1_account_holder = @Bank1AccountHolder,
                        bank2_name = @Bank2Name,
                        bank2_account_no = @Bank2AccountNo,
                        bank2_account_holder = @Bank2AccountHolder,
                        is_locked = @IsLocked;
                ", d);
            }
        }

        /// <summary>Convenience: sets the lock flag on the current row without touching any other field.</summary>
        public void Lock()
        {
            var d = Get();
            d.IsLocked = true;
            Save(d);
        }
    }
}