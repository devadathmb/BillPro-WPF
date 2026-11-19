using System.Data;
using Dapper;

namespace BillPro.DataAccess
{
    // ═══════════════════════════════════════════════════════════════════════
    // RESERVED DEFAULTS
    //
    // Every "special case" primary key, category code, or head code the app
    // relies on. Any time code needs a hardcoded ID/code that means "the
    // Cash head", "the Banks category", "the opening stock category", etc —
    // it references a constant from here, never a bare literal or a
    // name/LIKE match (name matches drift silently the moment someone
    // renames a category in the UI; a fixed code can't).
    //
    // Each table with preset rows gets its own Seed*() function below,
    // using INSERT OR IGNORE so it's safe to call on every startup — a
    // no-op once seeded, never overwrites a name the user has since
    // changed. Call the relevant Seed*() once from DatabaseBootstrapper,
    // after the owning table's CREATE TABLE has run.
    // ═══════════════════════════════════════════════════════════════════════
    public static class ReservedDefaults
    {
        // ac_category_master (accounts.db) — reserved codes 1-24;
        // 101+ is user territory (AccountCategoryRepository.GetNextCategoryCode).
        public const int OpeningStockCategoryCode = 1;
        public const int ClosingStockCategoryCode = 2;
        public const int VendorCustomerCategoryCode = 3;   // "Creditor/Debtor"
        public const int DefaultHeadsCategoryCode = 4;     // internal-only, houses the Cash head

        public const int CapitalAccountCategoryCode = 10;
        public const int CurrentAccountCategoryCode = 11;
        public const int LoansCategoryCode = 12;
        public const int KuriesCategoryCode = 13;

        /// <summary>The one category every "pick a bank" combo across the
        /// app (Sales, Purchase, Sales/Purchase Return, Payment, Receipt
        /// entry screens, and any bank-filtered report) must filter
        /// ac_head_master.ac_category_id against. Filter by this code, never
        /// by category_name — a name match breaks the instant a user
        /// renames the category from Settings.</summary>
        public const int BanksCategoryCode = 14;

        public const int OtherAssetsCategoryCode = 15;
        public const int OtherLiabilitiesCategoryCode = 16;
        public const int PurchaseCategoryCode = 17;
        public const int PurchaseReturnCategoryCode = 18;
        public const int SalesCategoryCode = 19;
        public const int SalesReturnCategoryCode = 20;
        public const int SalesTaxCategoryCode = 21;
        public const int OtherExpensesCategoryCode = 22;
        public const int OtherIncomeCategoryCode = 23;
        public const int RentalServiceCategoryCode = 24;

        // ac_category_master — auto-created dynamically by TaxLedgerRepository
        // the first time a tax rate is posted, so it has no fixed code to
        // seed here. Listed for visibility only.
        public const string TaxCategoryName = "Tax Ledgers";

        // ac_head_master (accounts.db) — ac_head_code "1"-"999" reserved;
        // 1001+ is user territory (AccountHeadRepository.GetNextAcHeadCode).
        public const string CashHeadCode = "1";

        // ac_head_master — ac_group_id values.
        public const int BalanceSheetGroupId = 0;
        public const int TradingDirectGroupId = 1;
        public const int TaxIndirectGroupId = 2;

        // ac_head_master — fixed-code series ranges for auto-created
        // per-tax-rate heads (base+1 .. base+99 each).
        public const int SalesLedgerSeriesBase = 400;
        public const int PurchaseLedgerSeriesBase = 500;
        public const int SalesReturnLedgerSeriesBase = 600;
        public const int PurchaseReturnLedgerSeriesBase = 700;
        public const int TaxCollectedLedgerSeriesBase = 800;
        public const int TaxPaidLedgerSeriesBase = 900;
        public const int DiscountRoundOffCodeOffset = 50;
        public const int ReceiptPaymentSeriesBase = 350;

        // item_category_master (inventory.db) — reserved code 1;
        // 101+ is user territory (ItemCategoryRepository.GetNextCategoryCode).
        public const int GeneralItemCategoryCode = 1;

        // company_master (inventory.db) — company_code is TEXT; reserved "1";
        // 101+ is user territory (CompanyMasterRepository.GetNextCompanyCode).
        public const string GeneralCompanyCode = "1";

        // ─────────────────────────────────────────────────────────────────
        // app_users (inventory.db) — two accounts seeded on first run only
        // (table empty). UserRepository.InitializeDatabase reads these
        // rather than hardcoding credentials inline, so both logins have
        // one place to look/change.
        // ─────────────────────────────────────────────────────────────────

        // The original default login — kept exactly as it was, now seeded
        // with the Admin role (not Developer). Change this password from
        // the Change Password screen once you've logged in as Developer.
        public const string DefaultAdminUsername = "user3";
        public const string DefaultAdminDisplayName = "Administrator";
        public const string DefaultAdminPassword = "u3";

        // The single Developer account — the only login that can open
        // Company Details or create Admin-level users. Deliberately left
        // as an obvious placeholder rather than a guessable real password:
        // fill both of these in yourself before the app is ever deployed,
        // then treat this file the way you'd treat any other secret (don't
        // commit your real values to a public/shared repo).
        public const string DeveloperUsername = "DeveloperDev";
        public const string DeveloperDisplayName = "Developer";
        public const string DeveloperPassword = "k1zwfsj7hw";

        // ac_category_master
        public static void SeedAcCategoryMaster(IDbConnection db)
        {
            db.Execute(@"INSERT OR IGNORE INTO ac_category_master (category_code, category_name) VALUES
                            (@Opening, 'Opening Stock'),
                            (@Closing, 'Closing Stock'),
                            (@VendorCustomer, 'Creditor/Debtor'),
                            (@Default, 'Default'),
                            (@Capital, 'Capital A/c.'),
                            (@Current, 'Current A/c.'),
                            (@Loans, 'Loans'),
                            (@Kuries, 'Kuries'),
                            (@Banks, 'Banks'),
                            (@OtherAssets, 'Other Assets'),
                            (@OtherLiabilities, 'Other Liabilities'),
                            (@Purchase, 'Purchase'),
                            (@PurchaseReturn, 'Purchase Return'),
                            (@Sales, 'Sales'),
                            (@SalesReturn, 'Sales Return'),
                            (@SalesTax, 'Sales Tax'),
                            (@OtherExpenses, 'Other Expenses'),
                            (@OtherIncome, 'Other Income'),
                            (@RentalService, 'RENTAL SERVICE')",
                new
                {
                    Opening = OpeningStockCategoryCode,
                    Closing = ClosingStockCategoryCode,
                    VendorCustomer = VendorCustomerCategoryCode,
                    Default = DefaultHeadsCategoryCode,
                    Capital = CapitalAccountCategoryCode,
                    Current = CurrentAccountCategoryCode,
                    Loans = LoansCategoryCode,
                    Kuries = KuriesCategoryCode,
                    Banks = BanksCategoryCode,
                    OtherAssets = OtherAssetsCategoryCode,
                    OtherLiabilities = OtherLiabilitiesCategoryCode,
                    Purchase = PurchaseCategoryCode,
                    PurchaseReturn = PurchaseReturnCategoryCode,
                    Sales = SalesCategoryCode,
                    SalesReturn = SalesReturnCategoryCode,
                    SalesTax = SalesTaxCategoryCode,
                    OtherExpenses = OtherExpensesCategoryCode,
                    OtherIncome = OtherIncomeCategoryCode,
                    RentalService = RentalServiceCategoryCode
                });
        }

        // ac_head_master
        public static void SeedAcHeadMaster(IDbConnection db)
        {
            db.Execute(@"INSERT OR IGNORE INTO ac_head_master (ac_head_code, ac_head_name, ac_category_id, ac_group_id)
                         VALUES (@Code, 'Cash', @Category, @Group)",
                new { Code = CashHeadCode, Category = DefaultHeadsCategoryCode, Group = BalanceSheetGroupId });
        }

        /// <summary>Runs both accounts.db seed functions, in dependency
        /// order (categories before heads, since heads reference a
        /// category). Call once at startup against an accounts.db connection.</summary>
        public static void SeedAccountsDb(IDbConnection db)
        {
            SeedAcCategoryMaster(db);
            SeedAcHeadMaster(db);
        }

        // item_category_master
        public static void SeedItemCategoryMaster(IDbConnection db)
        {
            db.Execute(@"INSERT OR IGNORE INTO item_category_master (item_category_code, item_category_name)
                         VALUES (@Code, 'GENERAL')",
                new { Code = GeneralItemCategoryCode });
        }

        // company_master
        public static void SeedCompanyMaster(IDbConnection db)
        {
            db.Execute(@"INSERT OR IGNORE INTO company_master (company_code, company_name)
                         VALUES (@Code, 'GENERAL')",
                new { Code = GeneralCompanyCode });
        }

        /// <summary>Runs both inventory.db seed functions. Call once at
        /// startup against an inventory.db (main) connection.</summary>
        public static void SeedInventoryDb(IDbConnection db)
        {
            SeedItemCategoryMaster(db);
            SeedCompanyMaster(db);
        }
    }
}