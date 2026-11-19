using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public partial class ReceiptRepository
    {
        private readonly FinancialSyncService _financialSync = new FinancialSyncService();
        private readonly TaxLedgerRepository _taxLedgerRepo = new TaxLedgerRepository();

        private string ReceiptVoucherRef(int receiptNo) => $"RCPT-{receiptNo}";

        private void PostToAccounts(ReceiptEntry entry, string currentUser)
        {
            var voucher = BuildVoucher(entry);

            try
            {
                _financialSync.Post(voucher, currentUser);
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(db, "receipt_entry_details", "receipt_no=@No",
                        new { No = entry.ReceiptNo });
            }
            catch
            {
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(db, "receipt_entry_details", "receipt_no=@No",
                        new { No = entry.ReceiptNo });
            }
        }

        private void ReverseInAccounts(int receiptNo)
        {
            var entry = GetByReceiptNo(receiptNo);
            if (entry == null) return;

            try
            {
                _financialSync.Reverse(TransactionType.Receipt, ReceiptVoucherRef(receiptNo), entry.ReceiptDate);
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(db, "receipt_entry_details", "receipt_no=@No", new { No = receiptNo });
            }
            catch
            {
                using (IDbConnection db = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(db, "receipt_entry_details", "receipt_no=@No", new { No = receiptNo });
            }
        }

        private FinancialVoucher BuildVoucher(ReceiptEntry entry)
        {
            var legs = new List<SettlementLeg>();

            // The customer/party head, credited for the amount entered on
            // the voucher (ReceiptAmount) — this is the full amount cleared
            // off their receivable. CashDiscount is NOT added on top: it is
            // a portion carved out of ReceiptAmount, not an amount over and
            // above it (entering 2000 receipt + 100 discount means the
            // customer's balance drops by 2000 total, not 2100).
            int partyHeadId = GetAcHeadIdByCode(entry.AcHeadCode);
            if (partyHeadId != 0)
                legs.Add(new SettlementLeg
                {
                    LedgerId = partyHeadId,
                    CreditAmount = (double)entry.ReceiptAmount
                });

            // Where the money actually landed — only ReceiptAmount minus the
            // discount actually reaches the cash/bank account (2000 receipt
            // with 100 discount means only 1900 physically comes in).
            decimal actualCashReceived = entry.ReceiptAmount - entry.CashDiscount;

            if (!string.IsNullOrEmpty(entry.BankAcHeadCode))
            {
                int bankHeadId = GetAcHeadIdByCode(entry.BankAcHeadCode);
                if (bankHeadId != 0)
                    legs.Add(new SettlementLeg { LedgerId = bankHeadId, DebitAmount = (double)actualCashReceived });
            }
            else
            {
                int cashHeadId = GetOrCreateCashHeadId();
                if (cashHeadId != 0)
                    legs.Add(new SettlementLeg { LedgerId = cashHeadId, DebitAmount = (double)actualCashReceived });
            }

            // Cash discount allowed to the customer for early payment — a
            // real expense, debited to a fixed "Discount Allowed" head
            // (auto-created on first use).
            if (entry.CashDiscount != 0)
            {
                using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
                {
                    db.Open();
                    using (IDbTransaction tx = db.BeginTransaction())
                    {
                        int discountAllowedId = _taxLedgerRepo.GetOrCreateDiscountAllowedHead(db, tx);
                        tx.Commit();
                        legs.Add(new SettlementLeg { LedgerId = discountAllowedId, DebitAmount = (double)entry.CashDiscount });
                    }
                }
            }

            return new FinancialVoucher
            {
                Type = TransactionType.Receipt,
                VoucherRef = ReceiptVoucherRef(entry.ReceiptNo),
                EntryDate = entry.ReceiptDate,
                Lines = new List<FinancialVoucherLine>(),
                SettlementLegs = legs
            };
        }

        // Delegates to TaxLedgerRepository's canonical Cash-head lookup,
        // which actually CREATES the head (reserved code "1") when missing —
        // see SalesRepository.Accounts.cs for why the old lookup-only body
        // was the root cause of cash-in-hand under-counting. Reuses the
        // _taxLedgerRepo field this class already has instead of a new
        // instance, since it's already declared here.
        private int GetOrCreateCashHeadId() => _taxLedgerRepo.GetOrCreateCashHeadId();

        private int GetAcHeadIdByCode(string acHeadCode)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
                return db.QueryFirstOrDefault<int?>(
                    "SELECT id FROM ac_head_master WHERE ac_head_code = @Code", new { Code = acHeadCode }) ?? 0;
        }

        /// <summary>
        /// Retries accounts posting for every receipt flagged
        /// accounts_synced = 0, optionally scoped to a receipt-date range
        /// (both null = every pending row, the startup-sweep default).
        /// A cancelled-but-still-pending receipt gets a Reverse retry;
        /// everything else gets a Post retry — same convention as
        /// SalesReturnRepository.ResyncPending.
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
                    ? "SELECT receipt_no FROM receipt_entry_details WHERE 1=1"
                    : "SELECT receipt_no FROM receipt_entry_details WHERE accounts_synced = 0";
                if (fromDate.HasValue) sql += " AND receipt_date >= @From";
                if (toDate.HasValue) sql += " AND receipt_date <= @To";

                pending = db.Query<int>(sql,
                    new { From = fromDate?.ToString("yyyy-MM-dd"), To = toDate?.ToString("yyyy-MM-dd") }).AsList();
            }
            outcome.Pending = pending.Count;

            foreach (var receiptNo in pending)
            {
                var entry = GetByReceiptNo(receiptNo);
                if (entry == null) continue;

                try
                {
                    if (entry.CancelStatus)
                        _financialSync.Reverse(TransactionType.Receipt, ReceiptVoucherRef(receiptNo), entry.ReceiptDate);
                    else
                        _financialSync.Post(BuildVoucher(entry), currentUser);

                    using (IDbConnection db = DatabaseConfig.GetConnection())
                        AccountsSyncStatus.MarkSynced(db, "receipt_entry_details", "receipt_no=@No", new { No = receiptNo });
                    outcome.Succeeded++;
                }
                catch (Exception ex)
                {
                    outcome.FailedRefs.Add($"{ReceiptVoucherRef(receiptNo)} — {ex.Message}");
                }
            }

            return outcome;
        }
    }
}