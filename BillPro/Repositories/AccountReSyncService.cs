using System;
using System.Collections.Generic;

namespace BillPro.Repositories
{
    public class ResyncResult
    {
        public int TotalPending { get; set; }
        public int Succeeded { get; set; }
        public List<string> FailedRefs { get; set; } = new List<string>();
    }

    /// <summary>
    /// Sweeps every bill flagged accounts_synced = 0 across all four
    /// transaction types and retries FinancialSyncService.Post/Reverse for
    /// each. Each repo exposes its own ResyncPending(currentUser, from, to)
    /// — this class just aggregates the results for one status message.
    ///
    /// Call ResyncAll(currentUser) with no date range once on app startup
    /// (silent — only surface a notification if something is still pending
    /// afterward), and again with a from/to range from the manual
    /// "Resync to Accounts" settings form for the user to retry on demand.
    /// </summary>
    public class AccountsResyncService
    {
        private readonly SalesRepository _salesRepo;
        private readonly PurchaseRepository _purchaseRepo;
        private readonly SalesReturnRepository _salesReturnRepo;
        private readonly PurchaseReturnRepository _purchaseReturnRepo;
        private readonly ReceiptRepository _receiptRepo;
        private readonly PaymentRepository _paymentRepo;

        public AccountsResyncService(
            SalesRepository salesRepo,
            PurchaseRepository purchaseRepo,
            SalesReturnRepository salesReturnRepo,
            PurchaseReturnRepository purchaseReturnRepo,
            ReceiptRepository receiptRepo,
            PaymentRepository paymentRepo)
        {
            _salesRepo = salesRepo;
            _purchaseRepo = purchaseRepo;
            _salesReturnRepo = salesReturnRepo;
            _purchaseReturnRepo = purchaseReturnRepo;
            _receiptRepo = receiptRepo;
            _paymentRepo = paymentRepo;
        }

        public ResyncResult ResyncAll(string currentUser, DateTime? fromDate = null, DateTime? toDate = null, bool forceAll = false)
        {
            var result = new ResyncResult();

            TryResync(() => _salesRepo.ResyncPending(currentUser, fromDate, toDate, forceAll), result);
            TryResync(() => _purchaseRepo.ResyncPending(currentUser, fromDate, toDate, forceAll), result);
            TryResync(() => _salesReturnRepo.ResyncPending(currentUser, fromDate, toDate, forceAll), result);
            TryResync(() => _purchaseReturnRepo.ResyncPending(currentUser, fromDate, toDate, forceAll), result);
            TryResync(() => _receiptRepo.ResyncPending(currentUser, fromDate, toDate, forceAll), result);
            TryResync(() => _paymentRepo.ResyncPending(currentUser, fromDate, toDate, forceAll), result);

            return result;
        }

        private void TryResync(Func<ResyncOutcome> resyncCall, ResyncResult aggregate)
        {
            try
            {
                var outcome = resyncCall();
                aggregate.TotalPending += outcome.Pending;
                aggregate.Succeeded += outcome.Succeeded;
                aggregate.FailedRefs.AddRange(outcome.FailedRefs);
            }
            catch
            {
                // accounts.db unreachable entirely (e.g. file locked) — leave
                // everything flagged pending, next sweep (startup or manual) retries.
            }
        }
    }

    // Returned by each repo's own ResyncPending(currentUser, from, to).
    public class ResyncOutcome
    {
        public int Pending { get; set; }
        public int Succeeded { get; set; }
        public List<string> FailedRefs { get; set; } = new List<string>();
    }
}