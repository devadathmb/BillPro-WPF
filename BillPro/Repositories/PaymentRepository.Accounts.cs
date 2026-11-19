using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public partial class PaymentRepository
    {
        private readonly FinancialSyncService _financialSync = new FinancialSyncService();

        private string PaymentVoucherRef(int paymentNo) => $"PAY-{paymentNo}";

        private void PostToAccounts(PaymentEntry entry, string currentUser)
        {
            var voucher = BuildVoucher(entry);

            try
            {
                _financialSync.Post(voucher, currentUser);
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(db, "payment_entry_details", "payment_no=@No",
                        new { No = entry.PaymentNo });
            }
            catch
            {
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(db, "payment_entry_details", "payment_no=@No",
                        new { No = entry.PaymentNo });
            }
        }

        private void ReverseInAccounts(int paymentNo)
        {
            var entry = GetByPaymentNo(paymentNo);
            if (entry == null) return;

            try
            {
                _financialSync.Reverse(TransactionType.Payment, PaymentVoucherRef(paymentNo), entry.PaymentDate);
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(db, "payment_entry_details", "payment_no=@No", new { No = paymentNo });
            }
            catch
            {
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(db, "payment_entry_details", "payment_no=@No", new { No = paymentNo });
            }
        }

        private FinancialVoucher BuildVoucher(PaymentEntry entry)
        {
            var legs = new List<SettlementLeg>();

            // The vendor/party head, debited — a payment reduces what we
            // owe them.
            int partyHeadId = GetAcHeadIdByCode(entry.AcHeadCode);
            if (partyHeadId != 0)
                legs.Add(new SettlementLeg { LedgerId = partyHeadId, DebitAmount = (double)entry.PaymentAmount });

            // Where the money actually went out from — credited.
            if (!string.IsNullOrEmpty(entry.BankAcHeadCode))
            {
                int bankHeadId = GetAcHeadIdByCode(entry.BankAcHeadCode);
                if (bankHeadId != 0)
                    legs.Add(new SettlementLeg { LedgerId = bankHeadId, CreditAmount = (double)entry.PaymentAmount });
            }
            else
            {
                int cashHeadId = GetOrCreateCashHeadId();
                if (cashHeadId != 0)
                    legs.Add(new SettlementLeg { LedgerId = cashHeadId, CreditAmount = (double)entry.PaymentAmount });
            }

            return new FinancialVoucher
            {
                Type = TransactionType.Payment,
                VoucherRef = PaymentVoucherRef(entry.PaymentNo),
                EntryDate = entry.PaymentDate,
                Lines = new List<FinancialVoucherLine>(),
                SettlementLegs = legs
            };
        }

        // Delegates to TaxLedgerRepository's canonical Cash-head lookup,
        // which actually CREATES the head (reserved code "1") when missing —
        // see SalesRepository.Accounts.cs for why the old lookup-only body
        // was the root cause of cash-in-hand under-counting.
        private int GetOrCreateCashHeadId() => new TaxLedgerRepository().GetOrCreateCashHeadId();

        private int GetAcHeadIdByCode(string acHeadCode)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
                return db.QueryFirstOrDefault<int?>(
                    "SELECT id FROM ac_head_master WHERE ac_head_code = @Code", new { Code = acHeadCode }) ?? 0;
        }

        /// <summary>
        /// Retries accounts posting for every payment flagged
        /// accounts_synced = 0, optionally scoped to a payment-date range
        /// (both null = every pending row, the startup-sweep default).
        ///
        /// forceAll: ignores the accounts_synced flag and reposts everything
        /// in the date range — see SalesRepository.ResyncPending for why
        /// this is needed for pre-existing data.
        /// </summary>
        public ResyncOutcome ResyncPending(string currentUser, DateTime? fromDate = null, DateTime? toDate = null, bool forceAll = false)
        {
            var outcome = new ResyncOutcome();
            List<int> pending;

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = forceAll
                    ? "SELECT payment_no FROM payment_entry_details WHERE 1=1"
                    : "SELECT payment_no FROM payment_entry_details WHERE accounts_synced = 0";
                if (fromDate.HasValue) sql += " AND payment_date >= @From";
                if (toDate.HasValue) sql += " AND payment_date <= @To";

                pending = db.Query<int>(sql,
                    new { From = fromDate?.ToString("yyyy-MM-dd"), To = toDate?.ToString("yyyy-MM-dd") }).AsList();
            }
            outcome.Pending = pending.Count;

            foreach (var paymentNo in pending)
            {
                var entry = GetByPaymentNo(paymentNo);
                if (entry == null) continue;

                try
                {
                    if (entry.CancelStatus)
                        _financialSync.Reverse(TransactionType.Payment, PaymentVoucherRef(paymentNo), entry.PaymentDate);
                    else
                        _financialSync.Post(BuildVoucher(entry), currentUser);

                    using (IDbConnection db = DatabaseConfig.GetConnection())
                        AccountsSyncStatus.MarkSynced(db, "payment_entry_details", "payment_no=@No", new { No = paymentNo });
                    outcome.Succeeded++;
                }
                catch (Exception ex)
                {
                    outcome.FailedRefs.Add($"{PaymentVoucherRef(paymentNo)} — {ex.Message}");
                }
            }

            return outcome;
        }
    }
}