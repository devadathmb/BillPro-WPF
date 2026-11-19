using System;
using System.Data;
using Dapper;
using BillPro.Models;
using BillPro.DataAccess;

namespace BillPro.Repositories
{
    public class VendorCustomerRepository : GenericRepository<VendorCustomerMaster>
    {
        // These are fixed for all Vendor/Customer heads created from this form.
        // ac_category_id → FK to ac_category_master (set to your Sundry Debtors/Creditors category code)
        // ac_group_id    → 0 = Balance Sheet
        private const int VcAcCategoryId = 3;
        private const int VcAcGroupId = 0;

        public VendorCustomerRepository() : base("vendor_customer_master")
        {
        }

        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"CREATE TABLE IF NOT EXISTS vendor_customer_master (
                                customer_code       TEXT PRIMARY KEY,
                                ac_head_code        TEXT,
                                customer_name       TEXT NOT NULL,
                                address1            TEXT,
                                address2            TEXT,
                                address3            TEXT,
                                place               TEXT,
                                pin                 TEXT,
                                phone               TEXT,
                                mobile              TEXT,
                                fax                 TEXT,
                                email               TEXT,
                                cst_no              TEXT,
                                tin_no              TEXT,
                                gst_no              TEXT,
                                contact_person      TEXT,
                                category            TEXT,
                                is_194q             TEXT,
                                created_by          TEXT,
                                created_date        TEXT,
                                last_modified_by    TEXT,
                                last_modified_date  TEXT)";
                db.Execute(sql);
            }
        }

        public string GetNextCustomerCode()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                // Cast to INTEGER in SQLite to ensure proper numeric sorting
                // This prevents the string sorting bug where "9999" is seen as > "10000"
                var maxCode = db.ExecuteScalar<int?>(
                    "SELECT MAX(CAST(customer_code AS INTEGER)) FROM vendor_customer_master");

                // If the table is empty or maxCode is null, start at 1001
                if (maxCode == null || maxCode == 0)
                {
                    return "1001";
                }

                // Otherwise, add 1 to the highest existing number
                int nextNum = maxCode.Value + 1;
                return nextNum.ToString();
            }
        }

        public bool CustomerExists(string customerName)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM vendor_customer_master WHERE customer_name = @CustomerName",
                    new { CustomerName = customerName }) > 0;
            }
        }

        /// <summary>
        /// Dual-DB save:
        ///   1. Fetches the next ac_head_code from AccountHeadRepository (accounts.db)
        ///   2. Inserts into ac_head_master (accounts.db) — name, address, phone, mobile + fixed category/group
        ///   3. Stamps that code onto the model and inserts into vendor_customer_master (inventory.db)
        /// </summary>
        public void AddCustomer(VendorCustomerMaster customer)
        {
            // Reuse the existing sequence logic — pure numeric, continuation of whatever was last.
            var acHeadRepo = new AccountHeadRepository();
            string generatedAcHeadCode = acHeadRepo.GetNextAcHeadCode();

            // ── STEP 2: Accounts DB ──────────────────────────────────────────────────
            using (IDbConnection accountsDb = DatabaseConfig.GetAccountsConnection())
            {

                // Only columns that exist in the ac_head_master schema are written here.
                // ac_category_id and ac_group_id are the fixed constants defined above.
                string accountsSql = @"
                    INSERT INTO ac_head_master
                        (ac_head_code, ac_head_name, ac_category_id, ac_group_id,
                         opening_credit, opening_debit,
                         address, phone, mobile)
                    VALUES
                        (@AcHeadCode, @AcHeadName, @AcCategoryId, @AcGroupId,
                         0, 0,
                         @Address, @Phone, @Mobile)";

                accountsDb.Execute(accountsSql, new
                {
                    AcHeadCode = generatedAcHeadCode,
                    AcHeadName = customer.CustomerName,
                    AcCategoryId = VcAcCategoryId,
                    AcGroupId = VcAcGroupId,
                    Address = customer.Address1,
                    Phone = customer.Phone,
                    Mobile = customer.Mobile
                });
            }

            // ── STEP 3: Inventory DB ─────────────────────────────────────────────────
            // Stamp the generated code onto the model so it is stored as the FK reference.
            customer.AcHeadCode = generatedAcHeadCode;

            using (IDbConnection inventoryDb = DatabaseConfig.GetConnection())
            {
                string inventorySql = @"
                    INSERT INTO vendor_customer_master
                        (customer_code, ac_head_code, customer_name,
                         address1, address2, address3, place, pin,
                         phone, mobile, fax, email,
                         cst_no, tin_no, gst_no,
                         contact_person, category, is_194q,
                         created_by, created_date)
                    VALUES
                        (@CustomerCode, @AcHeadCode, @CustomerName,
                         @Address1, @Address2, @Address3, @Place, @Pin,
                         @Phone, @Mobile, @Fax, @Email,
                         @CstNo, @TinNo, @GstNo,
                         @ContactPerson, @Category, @Is194Q,
                         @CreatedBy, @CreatedDate)";

                inventoryDb.Execute(inventorySql, customer);
            }
        }

        public void UpdateCustomer(VendorCustomerMaster customer)
        {
            // ── Inventory DB ─────────────────────────────────────────────────────────
            using (IDbConnection inventoryDb = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    UPDATE vendor_customer_master SET
                        ac_head_code        = @AcHeadCode,
                        customer_name       = @CustomerName,
                        address1            = @Address1,
                        address2            = @Address2,
                        address3            = @Address3,
                        place               = @Place,
                        pin                 = @Pin,
                        phone               = @Phone,
                        mobile              = @Mobile,
                        fax                 = @Fax,
                        email               = @Email,
                        cst_no              = @CstNo,
                        tin_no              = @TinNo,
                        gst_no              = @GstNo,
                        contact_person      = @ContactPerson,
                        category            = @Category,
                        is_194q             = @Is194Q,
                        last_modified_by    = @LastModifiedBy,
                        last_modified_date  = @LastModifiedDate
                    WHERE customer_code = @CustomerCode";

                inventoryDb.Execute(sql, customer);
            }

            // ── Accounts DB: keep ac_head_name in sync if the name was changed ──────
            using (IDbConnection accountsDb = DatabaseConfig.GetAccountsConnection())
            {
                accountsDb.Execute(
                    @"UPDATE ac_head_master SET
                        ac_head_name = @AcHeadName,
                        address      = @Address,
                        phone        = @Phone,
                        mobile       = @Mobile
                      WHERE ac_head_code = @AcHeadCode",
                    new
                    {
                        AcHeadName = customer.CustomerName,
                        Address = customer.Address1,
                        Phone = customer.Phone,
                        Mobile = customer.Mobile,
                        AcHeadCode = customer.AcHeadCode
                    });
            }
        }
    }
}