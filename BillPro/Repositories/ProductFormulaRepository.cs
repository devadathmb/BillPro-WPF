using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // 1. Inherit from GenericRepository and pass the Detail model
    public class ProductFormulaRepository : GenericRepository<ProductFormulaDetail>
    {
        // 2. Pass the exact database table name
        public ProductFormulaRepository() : base("product_formula")
        {
        }

        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    CREATE TABLE IF NOT EXISTS product_formula (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        main_product_code TEXT,
                        component_code TEXT,
                        component_name TEXT,
                        quantity REAL
                    );";
                db.Execute(sql);
            }
        }

        // ── Custom Item Queries ──────────────────────────────────────
        // Category codes: 0 = Finished Goods, 1 = Raw Material, 2 = Consumables, 3 = Non Stockable

        public List<ItemMaster> GetAllItems()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string query = "SELECT item_code AS ItemCode, item_name AS ItemName, category AS Category, unit_price_sales AS UnitPriceSales, unit_price_purchase AS UnitPricePurchase FROM item_master ORDER BY item_name";
                return db.Query<ItemMaster>(query).ToList();
            }
        }

        /// <summary>
        /// Items that a *formula can be defined for* / that a Production Entry can produce:
        /// Finished Goods (0) and Consumables (2) — NOT raw materials, NOT non-stockable.
        /// </summary>
        public List<ItemMaster> GetFormulaTargetItems()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string query = "SELECT item_code AS ItemCode, item_name AS ItemName, category AS Category, unit_price_sales AS UnitPriceSales, unit_price_purchase AS UnitPricePurchase FROM item_master WHERE category IN (0, 2) ORDER BY item_name";
                return db.Query<ItemMaster>(query).ToList();
            }
        }

        /// <summary>
        /// Items that can appear as *ingredients/components* inside a formula:
        /// Raw Materials (1) and Consumables (2).
        /// </summary>
        public List<ItemMaster> GetAllRawMaterials()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string query = "SELECT item_code AS ItemCode, item_name AS ItemName, category AS Category, unit_price_sales AS UnitPriceSales, unit_price_purchase AS UnitPricePurchase FROM item_master WHERE category IN (1, 2) ORDER BY item_name";
                return db.Query<ItemMaster>(query).ToList();
            }
        }

        public ItemMaster GetItem(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string query = "SELECT item_code AS ItemCode, item_name AS ItemName, category AS Category, unit_price_sales AS UnitPriceSales, unit_price_purchase AS UnitPricePurchase FROM item_master WHERE item_code = @Code LIMIT 1";
                return db.QueryFirstOrDefault<ItemMaster>(query, new { Code = itemCode });
            }
        }

        public List<ItemMaster> SearchItemsByName(string searchText, string categoryFilter)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                if (string.IsNullOrWhiteSpace(searchText)) return new List<ItemMaster>();

                string query = "SELECT item_code AS ItemCode, item_name AS ItemName, category AS Category, unit_price_sales AS UnitPriceSales, unit_price_purchase AS UnitPricePurchase FROM item_master WHERE LOWER(item_name) LIKE @Search";

                if (!string.IsNullOrWhiteSpace(categoryFilter))
                {
                    var cleanCategories = string.Join(",", categoryFilter.Split(','));
                    query += $" AND category IN ({cleanCategories})";
                }

                return db.Query<ItemMaster>(query, new { Search = $"%{searchText.ToLower()}%" }).ToList();
            }
        }

        // ── Custom Formula Queries ────────────────────────────────────

        public List<ProductFormulaDetail> GetFormulaDetails(string finishedGoodCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string query = @"
                    SELECT id AS Id, 
                           main_product_code AS FinishedGoodCode, 
                           component_code AS RawMaterialCode, 
                           component_name AS ItemName, 
                           quantity AS Quantity 
                    FROM product_formula 
                    WHERE main_product_code = @Code ORDER BY id";

                return db.Query<ProductFormulaDetail>(query, new { Code = finishedGoodCode }).ToList();
            }
        }

        public void SaveFormulaDetails(string finishedGoodCode, List<ProductFormulaDetail> details)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    try
                    {
                        // Wipe old formula
                        db.Execute("DELETE FROM product_formula WHERE main_product_code = @Code", new { Code = finishedGoodCode }, tx);

                        // Insert new formula rows
                        foreach (var row in details)
                        {
                            db.Execute(@"
                                INSERT INTO product_formula (main_product_code, component_code, component_name, quantity) 
                                VALUES (@MainCode, @CompCode, @CompName, @Qty)",
                                new
                                {
                                    MainCode = finishedGoodCode,
                                    CompCode = row.RawMaterialCode,
                                    CompName = row.ItemName,
                                    Qty = row.Quantity
                                }, tx);
                        }

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }
    }
}