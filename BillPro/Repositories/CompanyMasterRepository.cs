using System.Collections.Generic;
using System.Data;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // 1. Inherit from the GenericRepository and specify the Model
    public class CompanyMasterRepository : GenericRepository<CompanyMaster>
    {
        // 2. Pass the exact database table name to the base class
        public CompanyMasterRepository() : base("company_master")
        {
        }

        // NOTE: GetAll() and Delete() are now automatically inherited!

        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"CREATE TABLE IF NOT EXISTS company_master (
                                company_code TEXT PRIMARY KEY, 
                                company_name TEXT NOT NULL,
                                created_by TEXT,
                                created_date TEXT,
                                last_modified_by TEXT,
                                last_modified_date TEXT);";
                db.Execute(sql);

                try
                {
                    db.ExecuteScalar("SELECT created_by FROM company_master LIMIT 1");
                }
                catch
                {
                    db.Execute("ALTER TABLE company_master ADD COLUMN created_by TEXT;");
                    db.Execute("ALTER TABLE company_master ADD COLUMN created_date TEXT;");
                    db.Execute("ALTER TABLE company_master ADD COLUMN last_modified_by TEXT;");
                    db.Execute("ALTER TABLE company_master ADD COLUMN last_modified_date TEXT;");
                }
            }
        }

        public string GetNextCompanyCode()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int maxCode = db.ExecuteScalar<int?>("SELECT MAX(CAST(company_code AS INTEGER)) FROM company_master") ?? 0;
                return (maxCode < 101 ? 101 : maxCode + 1).ToString();
            }
        }

        public bool CompanyExists(string name)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<int>("SELECT COUNT(*) FROM company_master WHERE company_name = @Name", new { Name = name }) > 0;
            }
        }

        public void AddCompany(CompanyMaster company)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"INSERT INTO company_master (company_code, company_name, created_by, created_date) 
                               VALUES (@CompanyCode, @CompanyName, @CreatedBy, @CreatedDate)";
                db.Execute(sql, company);
            }
        }

        public void UpdateCompany(CompanyMaster company)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"UPDATE company_master 
                               SET company_name = @CompanyName, last_modified_by = @LastModifiedBy, last_modified_date = @LastModifiedDate 
                               WHERE company_code = @CompanyCode";
                db.Execute(sql, company);
            }
        }

        public bool IsCompanyInUse(string companyCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                try
                {
                    return db.ExecuteScalar<int>("SELECT COUNT(*) FROM item_master WHERE company_code = @Code", new { Code = companyCode }) > 0;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}