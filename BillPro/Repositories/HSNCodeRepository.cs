using System.Data;
using Microsoft.Data.Sqlite;
using Dapper;
using BillPro.Models;
using BillPro.DataAccess;

namespace BillPro.Repositories
{
    public class HSNCodeRepository : GenericRepository<HSNCodeMaster>
    {
        public HSNCodeRepository() : base("hsn_code_master")
        {
            InitializeDatabase();
        }

        public void InitializeDatabase()
        {
            using (var connection = DatabaseConfig.GetConnection())
            {
                connection.Open();
                var command = connection.CreateCommand();

                // Fully snake_case table definition
                command.CommandText = @"
                CREATE TABLE IF NOT EXISTS hsn_code_master (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    hsn_code TEXT NOT NULL UNIQUE,
                    item_description TEXT,  
                    tax_rate REAL NOT NULL
                )";
                command.ExecuteNonQuery();
            }
        }

        public void Save(HSNCodeMaster hsn)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();

                // If Id is 0, it's a new record
                if (hsn.Id == 0)
                {
                    // Notice the columns are snake_case, but the @Parameters match your C# Model!
                    string sql = @"
                        INSERT INTO hsn_code_master (hsn_code, item_description, tax_rate) 
                        VALUES (@HsnCode, @ItemDescription, @TaxRate)";

                    db.Execute(sql, hsn);
                }
                else
                {
                    // If Id > 0, we update the exact row, allowing them to freely change the text hsn_code!
                    string sql = @"
                        UPDATE hsn_code_master 
                        SET hsn_code = @HsnCode, 
                            item_description = @ItemDescription, 
                            tax_rate = @TaxRate 
                        WHERE id = @Id";

                    db.Execute(sql, hsn);
                }
            }
        }

        /// <summary>
        /// True if any item in item_master is currently registered under this
        /// HSN code — i.e. deleting the code would leave those items pointing
        /// at an HSN code that no longer exists. There's no direct billing-table
        /// reference to hsn_code (billing tables only store item_code), so
        /// "in use" is defined one level away via item_master.hsn_code, same
        /// spirit as ItemMasterRepository.IsItemInUse checking the tables that
        /// reference item_code directly.
        /// </summary>
        public bool IsHsnCodeInUse(string hsnCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                try
                {
                    return db.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM item_master WHERE hsn_code = @Code",
                        new { Code = hsnCode }) > 0;
                }
                catch
                {
                    // Table missing / not yet created — nothing can be using it.
                    return false;
                }
            }
        }
    }
}