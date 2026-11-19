// AccountCategoryRepository.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public class AccountCategoryRepository : AccountsGenericRepository<AccountCategory>
    {
        // Reserved category codes now live in BillPro.DataAccess.ReservedDefaults
        // (OpeningStockCategoryCode / ClosingStockCategoryCode / etc) —
        // kept here too, pointing at the same constants, so existing
        // callers of AccountCategoryRepository.OpeningStockCategoryCode /
        // ClosingStockCategoryCode don't need to change.
        public const int OpeningStockCategoryCode = ReservedDefaults.OpeningStockCategoryCode;
        public const int ClosingStockCategoryCode = ReservedDefaults.ClosingStockCategoryCode;

        public AccountCategoryRepository() : base("ac_category_master")
        {
            InitializeDatabase();
        }

        public void InitializeDatabase()
        {
            string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataFolder);

            // Routing to accounts.db
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();

                string sql = @"CREATE TABLE IF NOT EXISTS ac_category_master (
                                category_code INTEGER PRIMARY KEY,
                                category_name TEXT NOT NULL,
                                created_by TEXT,
                                created_date TEXT,
                                last_modified_by TEXT,
                                last_modified_date TEXT);";
                db.Execute(sql);

                // Preset rows (Opening Stock, Closing Stock, Vendor/Customer,
                // Default, Banks, etc.) are seeded centrally — see
                // BillPro.DataAccess.ReservedDefaults.SeedAcCategoryMaster,
                // called once from DatabaseBootstrapper. Not duplicated here.
            }
        }

        public int GetNextCategoryCode()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                int maxCode = db.ExecuteScalar<int?>("SELECT MAX(category_code) FROM ac_category_master") ?? 0;
                return maxCode < 101 ? 101 : maxCode + 1;
            }
        }

        public bool CategoryExists(string name)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.ExecuteScalar<int>("SELECT COUNT(*) FROM ac_category_master WHERE category_name = @Name", new { Name = name }) > 0;
            }
        }

        public void AddCategory(AccountCategory category)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                string sql = @"INSERT INTO ac_category_master (category_code, category_name, created_by, created_date) 
                               VALUES (@CategoryCode, @CategoryName, @CreatedBy, @CreatedDate)";
                db.Execute(sql, category);
            }
        }

        public void UpdateCategory(AccountCategory category)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                string sql = @"UPDATE ac_category_master 
                               SET category_name = @CategoryName, last_modified_by = @LastModifiedBy, last_modified_date = @LastModifiedDate 
                               WHERE category_code = @CategoryCode";
                db.Execute(sql, category);
            }
        }

        /// <summary>
        /// True if any account head in ac_head_master is currently filed
        /// under this category — i.e. renaming/editing or deleting the
        /// category would orphan or silently reclassify those heads.
        /// Previously this queried a column ("category_code") that doesn't
        /// exist on ac_head_master at all (the real FK is ac_category_id),
        /// so it always returned false via the catch block below and never
        /// actually blocked anything — fixed here.
        /// </summary>
        public bool IsCategoryInUse(int code)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                try
                {
                    return db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM ac_head_master WHERE ac_category_id = @Code",
                        new { Code = code }) > 0;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}