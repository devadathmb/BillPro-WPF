using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess; 

namespace BillPro.Repositories
{
    // The <T> makes this generic so it can handle ANY model (ItemMaster, CompanyMaster, etc.)
    public class GenericRepository<T> where T : class
    {
        // Protected so inherited classes can use it if needed
        protected readonly string _tableName;

        // The constructor forces us to pass the table name when we create the specific repository
        public GenericRepository(string tableName)
        {
            _tableName = tableName;
        }

        // --- THE UNIVERSAL METHODS ---

        public List<T> GetAll()
        {
            // Note: Replace "DataAccess.GetConnection()" with however your project 
            // currently opens its SQLite connection!
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<T>($"SELECT * FROM {_tableName}").ToList();
            }
        }

        public T GetById(string idColumn, object idValue)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<T>(
                    $"SELECT * FROM {_tableName} WHERE {idColumn} = @Id",
                    new { Id = idValue });
            }
        }

        public void Delete(string idColumn, object idValue)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute(
                    $"DELETE FROM {_tableName} WHERE {idColumn} = @Id",
                    new { Id = idValue });
            }
        }
    }
}