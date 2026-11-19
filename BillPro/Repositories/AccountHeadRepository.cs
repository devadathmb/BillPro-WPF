// AccountHeadRepository.cs
using System.Data;
using Microsoft.Data.Sqlite;
using Dapper;
using BillPro.Models;
using BillPro.DataAccess;
using System.Collections.Generic;
using System.Linq;

namespace BillPro.Repositories
{
    public class AccountHeadRepository : AccountsGenericRepository<AccountHeadMaster>
    {
        public AccountHeadRepository() : base("ac_head_master")
        {
            InitializeDatabase();
        }

        public void InitializeDatabase()
        {
            using (var connection = DatabaseConfig.GetAccountsConnection())
            {
                connection.Open();
                var command = connection.CreateCommand();

                command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ac_head_master (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ac_head_code TEXT NOT NULL UNIQUE,
                    ac_head_name TEXT NOT NULL,
                    ac_category_id INTEGER,
                    ac_group_id INTEGER,
                    opening_credit REAL DEFAULT 0,
                    opening_debit REAL DEFAULT 0,
                    address TEXT,
                    phone TEXT,
                    mobile TEXT
                )";
                command.ExecuteNonQuery();

                AddColumnIfMissing(connection, "gst_no", "TEXT");
            }
        }

        private void AddColumnIfMissing(IDbConnection db, string columnName, string columnDef)
        {
            try
            {
                db.Execute($"ALTER TABLE ac_head_master ADD COLUMN {columnName} {columnDef};");
            }
            catch (System.Exception)
            {
                // Column already exists.
            }
        }

        public void Save(AccountHeadMaster acHead)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();

                if (acHead.Id == 0)
                {
                    string sql = @"
                        INSERT INTO ac_head_master (
                            ac_head_code, ac_head_name, ac_category_id, ac_group_id,
                            opening_credit, opening_debit, address, phone, mobile, gst_no
                        ) 
                        VALUES (
                            @AcHeadCode, @AcHeadName, @AcCategoryId, @AcGroupId,
                            @OpeningCredit, @OpeningDebit, @Address, @Phone, @Mobile, @GstNo
                        )";
                    db.Execute(sql, acHead);
                }
                else
                {
                    string sql = @"
                        UPDATE ac_head_master 
                        SET ac_head_code = @AcHeadCode, 
                            ac_head_name = @AcHeadName, 
                            ac_category_id = @AcCategoryId,
                            ac_group_id = @AcGroupId,
                            opening_credit = @OpeningCredit,
                            opening_debit = @OpeningDebit,
                            address = @Address,
                            phone = @Phone,
                            mobile = @Mobile,
                            gst_no = @GstNo
                        WHERE id = @Id";
                    db.Execute(sql, acHead);
                }
            }
        }

        public void UpdateAccountHead(AccountHeadMaster accountHead)
        {
            using (IDbConnection accountsDb = DatabaseConfig.GetAccountsConnection())
            {
                string accountsSql = @"
            UPDATE ac_head_master SET
                ac_head_name       = @AcHeadName,
                ac_category_id     = @AcCategoryId,
                ac_group_id        = @AcGroupId,
                opening_credit     = @OpeningCredit,
                opening_debit      = @OpeningDebit,
                address            = @Address,
                phone              = @Phone,
                mobile             = @Mobile,
                gst_no             = @GstNo
            WHERE ac_head_code = @AcHeadCode";

                accountsDb.Execute(accountsSql, accountHead);
            }

            using (IDbConnection inventoryDb = DatabaseConfig.GetConnection())
            {
                string inventorySql = @"
            UPDATE vendor_customer_master SET
                customer_name = @AcHeadName,
                address1      = @Address,
                phone         = @Phone,
                mobile        = @Mobile
            WHERE ac_head_code = @AcHeadCode";

                inventoryDb.Execute(inventorySql, new
                {
                    AcHeadName = accountHead.AcHeadName,
                    Address = accountHead.Address,
                    Phone = accountHead.Phone,
                    Mobile = accountHead.Mobile,
                    AcHeadCode = accountHead.AcHeadCode
                });
            }
        }

        public string GetNextAcHeadCode()
        {
            using (var db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();

                int? maxCode = db.ExecuteScalar<int?>(
                    "SELECT MAX(CAST(ac_head_code AS INTEGER)) FROM ac_head_master WHERE ac_head_code GLOB '[0-9]*'");

                int nextCode = System.Math.Max(maxCode ?? 1000, 1000) + 1;
                return nextCode.ToString();
            }
        }

        /// <summary>Codes "1"-"999" are system/reserved territory (Cash,
        /// auto-created tax-rate heads, etc — see
        /// BillPro.DataAccess.ReservedDefaults) — 1001+ is the first
        /// user-registered code (GetNextAcHeadCode's own floor). A head
        /// whose code falls under 1000 was never created from this screen
        /// and must never be edited or deleted from it, regardless of
        /// whether anything currently references it.</summary>
        public bool IsReservedHeadCode(string acHeadCode)
        {
            return int.TryParse(acHeadCode, out int code) && code < 1000;
        }

        /// <summary>
        /// True if this a/c head is referenced anywhere outside
        /// ac_head_master itself — as a vendor/customer's linked head, as
        /// the settlement bank on a saved Sales/Purchase/Return bill, as an
        /// external-expense head on a Purchase, or on a Receipt/Payment
        /// entry. Editing the category/group of a head that's actively
        /// posted against, or deleting it outright, would silently break
        /// or orphan those existing records — so this must be checked
        /// before either action is allowed.
        ///
        /// Best-effort by design: each check is wrapped individually so a
        /// table that hasn't been created yet on a fresh install (or an
        /// older DB missing a newer column) doesn't block every check —
        /// it's simply treated as "no references found there".
        /// </summary>
        public bool IsAcHeadInUse(string acHeadCode)
        {
            bool codeParses = int.TryParse(acHeadCode, out int numericCode);

            // ── accounts.db side ────────────────────────────────────────
            using (IDbConnection acDb = DatabaseConfig.GetAccountsConnection())
            {
                if (CountSafe(acDb, "SELECT COUNT(*) FROM customer_receipt_entry_details WHERE ac_head_code = @Code",
                        new { Code = acHeadCode }) > 0) return true;

                if (CountSafe(acDb, "SELECT COUNT(*) FROM payment_entry_details WHERE ac_head_code = @Code",
                        new { Code = acHeadCode }) > 0) return true;

                if (CountSafe(acDb, "SELECT COUNT(*) FROM journal_entry_details WHERE ac_head_code = @Code",
                        new { Code = acHeadCode }) > 0) return true;
            }

            // ── inventory.db side ───────────────────────────────────────
            using (IDbConnection invDb = DatabaseConfig.GetConnection())
            {
                if (CountSafe(invDb, "SELECT COUNT(*) FROM vendor_customer_master WHERE ac_head_code = @Code",
                        new { Code = acHeadCode }) > 0) return true;

                if (CountSafe(invDb, "SELECT COUNT(*) FROM purchase_external_expenses WHERE ac_head_code = @Code",
                        new { Code = acHeadCode }) > 0) return true;

                // bank_head_code columns are stored as INTEGER — only worth
                // checking if the code is actually numeric.
                if (codeParses)
                {
                    if (CountSafe(invDb, "SELECT COUNT(*) FROM sales_summary WHERE bank_head_code = @Code",
                            new { Code = numericCode }) > 0) return true;

                    if (CountSafe(invDb, "SELECT COUNT(*) FROM purchase_summary_details WHERE bank_head_code = @Code",
                            new { Code = numericCode }) > 0) return true;

                    if (CountSafe(invDb, "SELECT COUNT(*) FROM sales_return_summary WHERE bank_head_code = @Code",
                            new { Code = numericCode }) > 0) return true;

                    if (CountSafe(invDb, "SELECT COUNT(*) FROM purchase_return_summary WHERE bank_head_code = @Code",
                            new { Code = numericCode }) > 0) return true;
                }
            }

            return false;
        }

        private static int CountSafe(IDbConnection db, string sql, object param)
        {
            try { return db.ExecuteScalar<int?>(sql, param) ?? 0; }
            catch { return 0; } // table/column not present on this install yet
        }

        public List<AccountHeadMaster> GetAllForLookup()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AccountHeadMaster>(
                    @"SELECT id AS Id, ac_head_code AS AcHeadCode, ac_head_name AS AcHeadName,
                             ac_category_id AS AcCategoryId, ac_group_id AS AcGroupId,
                             opening_credit AS OpeningCredit, opening_debit AS OpeningDebit,
                             address AS Address, phone AS Phone, mobile AS Mobile, gst_no AS GstNo
                      FROM ac_head_master
                      ORDER BY ac_head_name").AsList();
            }
        }

        public List<AccountHeadMaster> GetBalanceSheetHeads()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AccountHeadMaster>(
                    @"SELECT id AS Id, ac_head_code AS AcHeadCode, ac_head_name AS AcHeadName,
                             ac_category_id AS AcCategoryId, ac_group_id AS AcGroupId,
                             opening_credit AS OpeningCredit, opening_debit AS OpeningDebit,
                             address AS Address, phone AS Phone, mobile AS Mobile, gst_no AS GstNo
                      FROM ac_head_master
                      WHERE ac_group_id = 0
                      ORDER BY ac_head_name").AsList();
            }
        }

        public void SaveOpeningBalances(IEnumerable<AccountHeadMaster> heads)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();
                using (var txn = db.BeginTransaction())
                {
                    const string sql = @"
                        UPDATE ac_head_master
                        SET opening_credit = @OpeningCredit,
                            opening_debit  = @OpeningDebit
                        WHERE id = @Id";

                    foreach (var head in heads)
                    {
                        db.Execute(sql, new { head.OpeningCredit, head.OpeningDebit, head.Id }, txn);
                    }

                    txn.Commit();
                }
            }
        }

        public List<AccountHeadMaster> GetHeadsByCategory(int categoryCode)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AccountHeadMaster>(
                    @"SELECT id AS Id, ac_head_code AS AcHeadCode, ac_head_name AS AcHeadName,
                     ac_category_id AS AcCategoryId, ac_group_id AS AcGroupId,
                     opening_credit AS OpeningCredit, opening_debit AS OpeningDebit,
                     address AS Address, phone AS Phone, mobile AS Mobile, gst_no AS GstNo
              FROM ac_head_master
              WHERE ac_category_id = @Code
              ORDER BY ac_head_name",
                    new { Code = categoryCode }).AsList();   // <-- added
            }
        }

        public List<AccountHeadMaster> GetBankAcHeads()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AccountHeadMaster>(
                    @"SELECT h.id AS Id, h.ac_head_code AS AcHeadCode, h.ac_head_name AS AcHeadName,
                             h.ac_category_id AS AcCategoryId, h.ac_group_id AS AcGroupId,
                             h.opening_credit AS OpeningCredit, h.opening_debit AS OpeningDebit,
                             h.address AS Address, h.phone AS Phone, h.mobile AS Mobile, h.gst_no AS GstNo
                      FROM ac_head_master h
                      INNER JOIN ac_category_master c ON c.category_code = h.ac_category_id
                      WHERE c.category_name LIKE '%Bank%'
                      ORDER BY h.ac_head_name").AsList();
            }
        }

        public List<AcHeadLookup> GetBankAcHeadLookups()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AcHeadLookup>(@"
                    SELECT ac_head_code AS AcHeadCode,
                           ac_head_name AS AcHeadName
                    FROM   ac_head_master
                    WHERE  ac_category_id = @BanksCategory
                    ORDER  BY ac_head_name",
                    new { BanksCategory = ReservedDefaults.BanksCategoryCode }).AsList();
            }
        }

        public decimal GetLedgerBalance(string acHeadCode)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                decimal opening = db.ExecuteScalar<decimal?>(
                    "SELECT opening_debit - opening_credit FROM ac_head_master WHERE ac_head_code = @Code",
                    new { Code = acHeadCode }) ?? 0m;

                decimal totalReceipts = 0m;
                decimal totalPayments = 0m;

                try
                {
                    totalReceipts = db.ExecuteScalar<decimal?>(
                        "SELECT SUM(receipt_amount + cash_discount) FROM customer_receipt_entry_details WHERE ac_head_code = @Code AND cancel_status = 0",
                        new { Code = acHeadCode }) ?? 0m;
                }
                catch { }

                try
                {
                    totalPayments = db.ExecuteScalar<decimal?>(
                        "SELECT SUM(payment_amount) FROM payment_entry_details WHERE ac_head_code = @Code AND cancel_status = 0",
                        new { Code = acHeadCode }) ?? 0m;
                }
                catch { }

                return opening - totalReceipts + totalPayments;
            }
        }
    }
}