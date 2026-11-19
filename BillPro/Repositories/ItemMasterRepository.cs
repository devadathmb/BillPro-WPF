using Dapper;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public class ItemMasterRepository : GenericRepository<ItemMaster>
    {
        public ItemMasterRepository() : base("item_master")
        {
        }

        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                // NOTE: opening_stock column removed — stock now lives only in
                // item_batch_details / stock_transaction_details (see StockRepository).
                string sql = @"
                    CREATE TABLE IF NOT EXISTS item_master (
                        item_code TEXT PRIMARY KEY, hsn_code TEXT, item_name TEXT, item_category_code TEXT,
                        company_code TEXT, mrp REAL DEFAULT 0, unit_price_sales REAL DEFAULT 0,
                        unit_price_purchase REAL DEFAULT 0, tax_rate REAL DEFAULT 0, qty_pouch REAL DEFAULT 0,
                        pouch_box REAL DEFAULT 0, re_order_level REAL DEFAULT 0,
                        rack TEXT, sales_unit TEXT, category INTEGER DEFAULT 0, created_by TEXT,
                        created_date TEXT, last_modified_by TEXT, last_modified_date TEXT, is_active INTEGER DEFAULT 0);
                    CREATE TABLE IF NOT EXISTS item_category_master (item_category_code TEXT PRIMARY KEY, item_category_name TEXT);
                    CREATE TABLE IF NOT EXISTS company_master (company_code TEXT PRIMARY KEY, company_name TEXT);";
                db.Execute(sql);
            }
        }

        public IEnumerable<CategoryItem> GetCategories()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<CategoryItem>("SELECT item_category_code AS CategoryCode, item_category_name AS CategoryName FROM item_category_master ORDER BY item_category_name");
            }
        }

        public IEnumerable<CompanyItem> GetCompanies()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<CompanyItem>("SELECT company_code AS CompanyCode, company_name AS CompanyName FROM company_master ORDER BY company_name");
            }
        }

        public bool ItemExists(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<long>("SELECT COUNT(*) FROM item_master WHERE item_code = @Code", new { Code = itemCode }) > 0;
            }
        }

        public void AddItem(ItemMaster item)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"INSERT INTO item_master (item_code, hsn_code, item_name, item_category_code, company_code, mrp, unit_price_sales, unit_price_purchase, tax_rate, qty_pouch, pouch_box, re_order_level, rack, sales_unit, category, created_by, created_date, is_active) 
                               VALUES (@ItemCode, @HsnCode, @ItemName, @ItemCategoryCode, @CompanyCode, @Mrp, @UnitPriceSales, @UnitPricePurchase, @TaxRate, @QtyPouch, @PouchBox, @ReOrderLevel, @Rack, @SalesUnit, @Category, @CreatedBy, @CreatedDate, @IsActive)";
                db.Execute(sql, item);
            }
        }

        public void UpdateItem(ItemMaster item)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"UPDATE item_master SET hsn_code = @HsnCode, item_name = @ItemName, item_category_code = @ItemCategoryCode, company_code = @CompanyCode, mrp = @Mrp, unit_price_sales = @UnitPriceSales, unit_price_purchase = @UnitPricePurchase, tax_rate = @TaxRate, qty_pouch = @QtyPouch, pouch_box = @PouchBox, re_order_level = @ReOrderLevel, rack = @Rack, sales_unit = @SalesUnit, category = @Category, last_modified_by = @LastModifiedBy, last_modified_date = @LastModifiedDate, is_active = @IsActive 
                               WHERE item_code = @ItemCode";
                db.Execute(sql, item);
            }
        }

        /// <summary>
        /// Replaces the old opening_stock column check. An item can't be deleted
        /// if it has any stock movement at all (opening, purchase, sale, etc.)
        /// </summary>
        public double GetCurrentStock(string itemCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<double?>(
                    "SELECT SUM(current_qty) FROM View_CurrentStock WHERE item_code = @Code",
                    new { Code = itemCode }) ?? 0;
            }
        }

        public bool IsItemInUse(string itemCode)
        {
            string[] tables = { "purchase_item_details", "gst_sales_item_details_wholesale", "gst_sales_item_details_retail", "gst_sales_item_details_interstate", "sales_return_item_details", "excess_stock_item_details", "short_stock_item_details" };
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                foreach (string table in tables)
                {
                    try
                    {
                        if (db.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table} WHERE item_code = @Code", new { Code = itemCode }) > 0)
                            return true;
                    }
                    catch { /* Table missing, skip */ }
                }
            }
            return false;
        }

        public string GetNextItemCode()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int maxCode = db.ExecuteScalar<int?>("SELECT MAX(CAST(item_code AS INTEGER)) FROM item_master") ?? 0;
                return (maxCode < 1001 ? 1001 : maxCode + 1).ToString();
            }
        }

        // ── FORMULA & SEARCH METHODS ─────────────────────────────────────────────

        public List<ItemMaster> SearchItemsByName(string prefix, string categoryFilter = "")
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string whereClause = string.IsNullOrWhiteSpace(prefix)
                    ? "1=1"
                    : "item_name LIKE @prefix";

                string catClause = string.IsNullOrWhiteSpace(categoryFilter)
                    ? ""
                    : " AND category IN (" + categoryFilter + ")";

                return db.Query<ItemMaster>(
                    "SELECT * FROM item_master WHERE " + whereClause + catClause +
                    " ORDER BY item_name LIMIT 100",
                    new { prefix = prefix.Trim() + "%" }).ToList();
            }
        }

        public List<ProductFormulaDetail> GetFormulaDetails(string finishedGoodCode)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<ProductFormulaDetail>(@"
            SELECT  p.id,
                    p.main_product_code  AS FinishedGoodCode,
                    p.component_code     AS RawMaterialCode,
                    p.component_name     AS ItemName,
                    p.quantity           AS Quantity
            FROM    product_formula p
            WHERE   p.main_product_code = @code
            ORDER BY p.id",
                    new { code = finishedGoodCode }).ToList();
            }
        }

        public void SaveFormulaDetails(string finishedGoodCode, List<ProductFormulaDetail> lines)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    db.Execute(
                        "DELETE FROM product_formula WHERE main_product_code = @code",
                        new { code = finishedGoodCode }, tx);

                    foreach (var line in lines)
                    {
                        db.Execute(@"
                    INSERT INTO product_formula
                        (main_product_code, component_code, component_name, quantity)
                    VALUES (@main, @comp, @name, @qty)",
                            new
                            {
                                main = finishedGoodCode,
                                comp = line.RawMaterialCode,
                                name = line.ItemName,
                                qty = line.Quantity
                            }, tx);
                    }
                    tx.Commit();
                }
            }
        }
    }
}