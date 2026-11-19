using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // 1. Inherit from GenericRepository
    public class ItemCategoryRepository : GenericRepository<ItemCategory>
    {
        // 2. Pass the exact database table name
        public ItemCategoryRepository() : base("item_category_master")
        {
        }

        public void InitializeDatabase()
        {
            // CRITICAL FIX: Dynamically create the data folder so it doesn't crash on other computers
            string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataFolder);

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"CREATE TABLE IF NOT EXISTS item_category_master (
                                item_category_code INTEGER PRIMARY KEY,
                                item_category_name TEXT NOT NULL,
                                created_by TEXT,
                                created_date TEXT,
                                last_modified_by TEXT,
                                last_modified_date TEXT);";
                db.Execute(sql);
            }
        }

        public int GetNextCategoryCode()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int maxCode = db.ExecuteScalar<int?>("SELECT MAX(item_category_code) FROM item_category_master") ?? 0;
                return maxCode < 101 ? 101 : maxCode + 1;
            }
        }

        public bool CategoryExists(string name)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<int>("SELECT COUNT(*) FROM item_category_master WHERE item_category_name = @Name", new { Name = name }) > 0;
            }
        }

        public void AddCategory(ItemCategory category)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"INSERT INTO item_category_master (item_category_code, item_category_name, created_by, created_date) 
                               VALUES (@ItemCategoryCode, @ItemCategoryName, @CreatedBy, @CreatedDate)";
                db.Execute(sql, category);
            }
        }

        public void UpdateCategory(ItemCategory category)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"UPDATE item_category_master 
                               SET item_category_name = @ItemCategoryName, last_modified_by = @LastModifiedBy, last_modified_date = @LastModifiedDate 
                               WHERE item_category_code = @ItemCategoryCode";
                db.Execute(sql, category);
            }
        }

        public bool IsCategoryInUse(int code)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                try
                {
                    return db.ExecuteScalar<int>("SELECT COUNT(*) FROM item_master WHERE category = @Code OR item_category_code = @Code", new { Code = code }) > 0;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}