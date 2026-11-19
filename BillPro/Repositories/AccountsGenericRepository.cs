using Dapper;
using BillPro.DataAccess; // Use your actual namespace here
using System.Collections.Generic;
using System.Data;

namespace BillPro.Repositories // Use your actual namespace here
{
    public class AccountsGenericRepository<T>
    {
        private readonly string _tableName;

        public AccountsGenericRepository(string tableName)
        {
            _tableName = tableName;
        }

        public IEnumerable<T> GetAll()
        {
            // Pointing to the ACCOUNTS database!
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<T>($"SELECT * FROM {_tableName}");
            }
        }
        public T GetById(string idColumn, object idValue)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.QueryFirstOrDefault<T>(
                    $"SELECT * FROM {_tableName} WHERE {idColumn} = @Id",
                    new { Id = idValue });
            }
        }

        public void Delete(string primaryKeyColumn, object idValue)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                // Using 'object' allows it to accept both strings and ints
                db.Execute($"DELETE FROM {_tableName} WHERE {primaryKeyColumn} = @Id", new { Id = idValue });
            }
        }
    }
}