using System;
using System.Windows;
using System.Windows.Controls;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Manual "Resync to Accounts" form. Blank From/To = sweep every pending
    // entry regardless of date (matches the silent startup sweep); a filled
    // range scopes the retry to that window only. Wraps AccountsResyncService
    // .ResyncAll, which just calls each of the four repos' ResyncPending in
    // turn and aggregates the totals.
    public partial class ResyncSettingsView : UserControl
    {
        private readonly AccountsResyncService _resyncService;
        private readonly FinancialSyncService _financialSync;

        public ResyncSettingsView()
        {
            InitializeComponent();

            _resyncService = new AccountsResyncService(
                new SalesRepository(),
                new PurchaseRepository(),
                new SalesReturnRepository(),
                new PurchaseReturnRepository(),
                new ReceiptRepository(),
                new PaymentRepository());

            _financialSync = new FinancialSyncService();
        }

        private void ChkForceAll_Checked(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This reposts EVERY entry in the selected date range to accounts.db, even ones already flagged as synced. " +
                "Existing postings for a re-posted entry are replaced (not duplicated), so it's safe to run more than once — " +
                "but it does require both From and To to be set first, so you don't accidentally resync your entire history.\n\n" +
                "Continue?",
                "Force Resync — Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                chkForceAll.IsChecked = false;
        }

        private void ChkForceAll_Unchecked(object sender, RoutedEventArgs e) { }

        private void BtnClearRange_Click(object sender, RoutedEventArgs e)
        {
            dpFromDate.SelectedDate = null;
            dpToDate.SelectedDate = null;
        }

        // Runs FinancialSyncService.MigrateAndRebuildDaybookSummary — drops
        // the old-shape daybook_summary if one is found, recreates it with
        // the current grouped/granular schema, and fully rebuilds it from
        // voucher_tax_postings (the audit trail, untouched by this). This
        // is a one-time action after upgrading to the new architecture, but
        // it's also safe to re-run any time as a general "Rebuild Daybook"
        // maintenance action — it always leaves the table in a consistent
        // state, just possibly wipes and rebuilds it for nothing if it was
        // already current.
        private void BtnMigrateDaybook_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will rebuild the Daybook Summary report cache from the underlying posting history. " +
                "If you're upgrading from an older version, the old summary table will be dropped and " +
                "recreated first. Your actual bills, receipts, and payments are never touched — this only " +
                "affects the report cache, and it's safe to run more than once.\n\n" +
                "Continue?",
                "Migrate & Rebuild Daybook Summary — Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            btnMigrateDaybook.IsEnabled = false;
            lblMigrateStatus.Text = "Running…";
            try
            {
                bool migrated = _financialSync.MigrateAndRebuildDaybookSummary();

                lblMigrateStatus.Text = migrated
                    ? "Migrated old table and rebuilt successfully."
                    : "Rebuilt successfully (no legacy table found).";

                MessageBox.Show(
                    migrated
                        ? "The old Daybook Summary table was found, replaced with the new schema, and fully rebuilt from posting history."
                        : "Daybook Summary was already on the current schema and has been fully rebuilt from posting history.",
                    "Migration Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                lblMigrateStatus.Text = "Failed — see error.";
                MessageBox.Show("Error migrating/rebuilding Daybook Summary:\n" + ex.Message,
                    "Migration Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnMigrateDaybook.IsEnabled = true;
            }
        }

        private void BtnResync_Click(object sender, RoutedEventArgs e)
        {
            bool forceAll = chkForceAll.IsChecked == true;
            DateTime? fromDate = dpFromDate.SelectedDate;
            DateTime? toDate = dpToDate.SelectedDate;

            if (forceAll && (fromDate == null || toDate == null))
            {
                MessageBox.Show("Force resync needs both a From and a To date — pick a range first.",
                    "Date Range Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnResync.IsEnabled = false;
            try
            {
                // TODO: swap in your actual logged-in username source if the
                // app tracks one (e.g. a static CurrentUser on MainWindow/App) —
                // this just matches the currentUser string that PostToAccounts
                // already threads through from each entry form's Save/Update.
                string currentUser = Environment.UserName;

                var result = _resyncService.ResyncAll(currentUser, fromDate, toDate, forceAll);

                lblPending.Text = result.TotalPending.ToString();
                lblSucceeded.Text = result.Succeeded.ToString();
                lblFailed.Text = result.FailedRefs.Count.ToString();

                lstFailed.ItemsSource = result.FailedRefs;
                bool anyFailed = result.FailedRefs.Count > 0;
                lblFailedHeader.Visibility = anyFailed ? Visibility.Visible : Visibility.Collapsed;
                lstFailed.Visibility = anyFailed ? Visibility.Visible : Visibility.Collapsed;

                if (result.TotalPending == 0)
                    MessageBox.Show("Nothing was pending — everything in range is already synced.",
                        "Resync Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                else if (!anyFailed)
                    MessageBox.Show($"Resync complete — all {result.Succeeded} pending entries posted successfully.",
                        "Resync Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"Resync finished — {result.Succeeded} succeeded, {result.FailedRefs.Count} still failing. See the list below.",
                        "Resync Finished With Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error running resync:\n" + ex.Message,
                    "Resync Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnResync.IsEnabled = true;
            }
        }
    }
}