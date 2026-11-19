using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using Dapper;
using BillPro.Models;
using BillPro.DataAccess;
using System.Linq;

namespace BillPro.Repositories
{
    // 1. Inherit from the GenericRepository and specify the Model
    public class VehicleRepository : GenericRepository<VehicleMaster>
    {
        // 2. Pass the exact database table name to the base class
        public VehicleRepository() : base("vehicle_master")
        {
        }

        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"CREATE TABLE IF NOT EXISTS vehicle_master (
                                vehicle_code TEXT PRIMARY KEY,
                                vehicle_number TEXT UNIQUE NOT NULL,
                                created_by TEXT,
                                created_date TEXT,
                                last_modified_by TEXT,
                                last_modified_date TEXT)";
                db.Execute(sql);
            }
        }

        public bool VehicleExists(string vehicleNumber)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<int>("SELECT COUNT(1) FROM vehicle_master WHERE vehicle_number = @VehicleNumber", new { VehicleNumber = vehicleNumber }) > 0;
            }
        }

        public string GetNextVehicleCode()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                var maxCode = db.ExecuteScalar<string>("SELECT MAX(vehicle_code) FROM vehicle_master");
                if (string.IsNullOrEmpty(maxCode)) return "V001";
                int nextNum = int.Parse(maxCode.Substring(1)) + 1;
                return "V" + nextNum.ToString("D3");
            }
        }

        public void AddVehicle(VehicleMaster vehicle)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"INSERT INTO vehicle_master (vehicle_code, vehicle_number, created_by, created_date) 
                               VALUES (@VehicleCode, @VehicleNumber, @CreatedBy, @CreatedDate)";
                db.Execute(sql, vehicle);
            }
        }

        public void UpdateVehicle(VehicleMaster vehicle)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"UPDATE vehicle_master 
                               SET vehicle_number = @VehicleNumber, last_modified_by = @LastModifiedBy, last_modified_date = @LastModifiedDate 
                               WHERE vehicle_code = @VehicleCode";
                db.Execute(sql, vehicle);
            }
        }

        public bool IsVehicleInUse(string vehicleCode)
        {
            // Update this query later based on your actual transaction table (e.g., delivery or billing)
            return false;
        }
    }
}