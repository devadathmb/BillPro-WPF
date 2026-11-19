using System;
using System.Collections.Generic;
using BillPro.Repositories;

namespace BillPro.DataAccess
{
    public static class DatabaseBootstrapper
    {
        /// <summary>
        /// Creates every table this app uses, once, before any window is
        /// shown — so no view can ever be the "first" one to touch a table
        /// that only some other repository's constructor would have
        /// created. Call this from App.xaml.cs (OnStartup), before
        /// MainWindow/LoginWindow is constructed.
        ///
        /// Order matters only where a repo's constructor field-initializes
        /// another repo (e.g. JournalEntryRepository -> FinancialSyncService
        /// -> TaxLedgerRepository) and that dependency queries a table
        /// eagerly. Doing accounts.db "master" tables (categories, heads)
        /// before anything that posts against them keeps that safe. If a
        /// new repo/table gets added later and this list isn't updated,
        /// worst case is back to today's behavior for that one table —
        /// this list is deliberately exhaustive so that doesn't happen.
        /// </summary>
        public static void InitializeAllDatabases()
        {
            var failures = new List<string>();

            // ── accounts.db "master" tables first — everything else in
            // accounts.db (heads, categories) is read by financial posting
            // code the moment any transactional repo is constructed. ──────
            Try(() => new AccountCategoryRepository().InitializeDatabase(), failures, "Account Categories");
            Try(() => new AccountHeadRepository().InitializeDatabase(), failures, "Account Heads");
            Try(() =>
            {
                using (var db = DatabaseConfig.GetAccountsConnection())
                    ReservedDefaults.SeedAccountsDb(db);
            }, failures, "Reserved Defaults (accounts.db)");

            // ── inventory.db master/setup tables ────────────────────────
            Try(() => new ItemCategoryRepository().InitializeDatabase(), failures, "Item Categories");
            Try(() => new CompanyMasterRepository().InitializeDatabase(), failures, "Companies");
            Try(() =>
            {
                using (var db = DatabaseConfig.GetConnection())
                    ReservedDefaults.SeedInventoryDb(db);
            }, failures, "Reserved Defaults (inventory.db)");
            Try(() => new ItemMasterRepository().InitializeDatabase(), failures, "Items");
            Try(() => new HSNCodeRepository().InitializeDatabase(), failures, "HSN Codes");
            Try(() => new UqcRepository().InitializeAndSeed(), failures, "UQC");
            Try(() => new VehicleRepository().InitializeDatabase(), failures, "Vehicles");
            Try(() => new VendorCustomerRepository().InitializeDatabase(), failures, "Vendors/Customers");
            // SundryPartyRepository has no InitializeDatabase — it only reads
            // through AccountHeadRepository/TaxLedgerRepository, both of
            // which are already initialized above.
            Try(() => new CompanyRepository().InitializeDatabase(), failures, "Company Details");
            Try(() => new ProductFormulaRepository().InitializeDatabase(), failures, "Product Formula");
            Try(() => new TaxLedgerRepository().InitializeDatabase(), failures, "Tax Ledger");

            // ── transactional tables ────────────────────────────────────
            Try(() => new SalesSeriesRepository().InitializeDatabase(), failures, "Sales Series");
            Try(() => new SalesRepository().InitializeDatabase(), failures, "Sales");
            Try(() => new SalesReturnRepository().InitializeDatabase(), failures, "Sales Return");
            Try(() => new PurchaseRepository().InitializeDatabase(), failures, "Purchase");
            Try(() => new PurchaseReturnRepository().InitializeDatabase(), failures, "Purchase Return");
            Try(() => new ReceiptRepository().InitializeDatabase(), failures, "Receipt");
            Try(() => new PaymentRepository().InitializeDatabase(), failures, "Payment");
            Try(() => new JournalEntryRepository().InitializeDatabase(), failures, "Journal Entry");
            Try(() => new ProductionRepository().InitializeDatabase(), failures, "Production");
            Try(() => new OpeningStockRepository().InitializeDatabase(), failures, "Opening Stock");
            Try(() => new StockMasterRepository().InitializeDatabase(), failures, "Stock");
            Try(() => new ExcessStockRepository().InitializeDatabase(), failures, "Excess Stock");
            Try(() => new ShortStockRepository().InitializeDatabase(), failures, "Short Stock");
            Try(() => new UserRepository().InitializeDatabase(), failures, "Users");

            if (failures.Count > 0)
            {
                // Don't hide this — a genuinely broken table def is a real
                // bug you want to see once, at startup, in one dialog,
                // instead of as N different "no such table" crashes later
                // depending on click order.
                System.Windows.MessageBox.Show(
                    "Some parts of the database could not be prepared:\n\n" +
                    string.Join("\n", failures) +
                    "\n\nThe app will continue, but related screens may not work correctly.",
                    "Startup Warning",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        private static void Try(Action init, List<string> failures, string label)
        {
            try
            {
                init();
            }
            catch (Exception ex)
            {
                failures.Add($"{label}: {ex.Message}");
            }
        }
    }
}