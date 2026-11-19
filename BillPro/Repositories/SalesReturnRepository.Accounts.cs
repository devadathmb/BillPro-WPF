using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public partial class SalesReturnRepository
    {
        private void PostToAccounts(SalesReturnSummary summary, List<SalesReturnItemDetail> items, string currentUser)
        {
            var voucher = BuildVoucher(summary, items);

            try
            {
                _financialSync.Post(voucher, currentUser);
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(acDb, "sales_return_summary", "entry_no=@E", new { E = summary.EntryNo });
            }
            catch
            {
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(acDb, "sales_return_summary", "entry_no=@E", new { E = summary.EntryNo });
            }
        }

        private FinancialVoucher BuildVoucher(SalesReturnSummary summary, List<SalesReturnItemDetail> items)
        {
            // summary.DiscountAmount is the persisted "line + bill% + cash"
            // combined total (see EntryView RecalcTotals — there's no
            // separate persisted column for the footer-only portion).
            // The line/grid discount is already netted into each item's
            // TaxableAmount above, so passing the combined total here would
            // double-count it on the accounts side. Accounts should only
            // see the bottom-section discount — bill% + cash — which is
            // exactly what the UI's Discount Total label (lblDiscountAmount)
            // shows. Back it out the same way: combined total minus the
            // sum of per-row discounts. Mirror of PurchaseReturnRepository.Accounts.cs.
            double lineDiscount = items.Sum(i => i.DiscountAmount);
            double footerDiscount = summary.DiscountAmount - lineDiscount;

            return new FinancialVoucher
            {
                Type = TransactionType.SalesReturn,
                VoucherRef = summary.FormattedEntryNo,
                EntryDate = summary.EntryDate.ToString("yyyy-MM-dd"),
                Lines = items.GroupBy(i => i.TaxPercent).Select(g => new FinancialVoucherLine
                {
                    TaxPercent = g.Key,
                    TaxableValue = g.Sum(i => i.TaxableAmount),
                    TaxAmount = g.Sum(i => i.TaxAmount)
                }).ToList(),
                SettlementLegs = BuildSettlementLegs(summary),
                DiscountAmount = footerDiscount,
                RoundOffAmount = summary.RoundOff
            };
        }

        // A sales return DECREASES what the customer owes (we're refunding
        // or reversing an earlier sale) — SalesReturn@X% + TaxPaid@X% are
        // debited by ResolveLedgers, so the settlement legs here are
        // CREDITED: cash/bank paid out to the customer, or their credit
        // balance reduced.
        //
        // LegType is tagged here (Cash / Bank / Party) — same reasoning as
        // SalesRepository.Accounts.cs: Cash/Bank group into daybook_summary
        // by day+series+ledger, the Customer leg always stays per-bill.
        private List<SettlementLeg> BuildSettlementLegs(SalesReturnSummary summary)
        {
            var legs = new List<SettlementLeg>();

            if (summary.ToCashAmount != 0)
            {
                int cashHeadId = GetOrCreateCashHeadId();
                if (cashHeadId != 0)
                    legs.Add(new SettlementLeg
                    {
                        LedgerId = cashHeadId,
                        CreditAmount = summary.ToCashAmount,
                        LegType = SettlementLegType.Cash
                    });
            }

            if (summary.ToBankAmount != 0 && summary.BankHeadCode != 0)
            {
                int bankHeadId = GetAcHeadIdByCode(summary.BankHeadCode.ToString());
                if (bankHeadId != 0)
                    legs.Add(new SettlementLeg
                    {
                        LedgerId = bankHeadId,
                        CreditAmount = summary.ToBankAmount,
                        LegType = SettlementLegType.Bank
                    });
            }

            if (summary.ToCreditAmount != 0 && !string.IsNullOrEmpty(summary.CustomerCode))
            {
                int customerHeadId = GetAcHeadIdForParty(summary.CustomerCode);
                if (customerHeadId != 0)
                    legs.Add(new SettlementLeg
                    {
                        LedgerId = customerHeadId,
                        CreditAmount = summary.ToCreditAmount,
                        LegType = SettlementLegType.Party
                    });
            }

            return legs;
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

        // Category ID every vendor/customer head is created under — must
        // match VendorCustomerRepository.VcAcCategoryId. Guards the two-hop
        // lookup below — see SalesRepository.Accounts.cs GetPartyAcHeadIdByCode
        // for why this filter is needed (ac_head_code is a global sequence
        // shared by every head type, so a bare code can coincidentally match
        // an unrelated head if the vendor_customer_master hop is ever
        // skipped).
        private const int VcAcCategoryId = 3;

        private int GetPartyAcHeadIdByCode(string acHeadCode)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
                return db.QueryFirstOrDefault<int?>(
                    "SELECT id FROM ac_head_master WHERE ac_head_code = @Code AND ac_category_id = @Cat",
                    new { Code = acHeadCode, Cat = VcAcCategoryId }) ?? 0;
        }

        // See SalesRepository.Accounts.cs GetAcHeadIdForParty for why this
        // two-hop lookup (customer_code -> ac_head_code -> ac_head_master.id)
        // is needed instead of treating CustomerCode as an ac_head_code.
        private int GetAcHeadIdForParty(string customerCode)
        {
            if (string.IsNullOrEmpty(customerCode)) return 0;

            string acHeadCode;
            using (IDbConnection db = DatabaseConfig.GetConnection())
                acHeadCode = db.QueryFirstOrDefault<string>(
                    "SELECT ac_head_code FROM vendor_customer_master WHERE customer_code = @Code",
                    new { Code = customerCode });

            return string.IsNullOrEmpty(acHeadCode) ? 0 : GetPartyAcHeadIdByCode(acHeadCode);
        }

        /// <summary>
        /// Retries accounts posting for every return flagged
        /// accounts_synced = 0, optionally scoped to an entry-date range
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
                    ? "SELECT entry_no FROM sales_return_summary WHERE 1=1"
                    : "SELECT entry_no FROM sales_return_summary WHERE accounts_synced = 0";
                if (fromDate.HasValue) sql += " AND entry_date >= @From";
                if (toDate.HasValue) sql += " AND entry_date <= @To";

                pending = db.Query<int>(sql,
                    new { From = fromDate?.ToString("yyyy-MM-dd"), To = toDate?.ToString("yyyy-MM-dd") }).AsList();
            }
            outcome.Pending = pending.Count;

            foreach (var entryNo in pending)
            {
                var summary = GetSummary(entryNo);
                if (summary == null) continue;
                var items = GetItems(entryNo);

                try
                {
                    if (summary.IsCancelled == 1)
                        _financialSync.Reverse(TransactionType.SalesReturn, summary.FormattedEntryNo,
                            summary.EntryDate.ToString("yyyy-MM-dd"));
                    else
                        _financialSync.Post(BuildVoucher(summary, items), currentUser);

                    using (IDbConnection acDb = DatabaseConfig.GetConnection())
                        AccountsSyncStatus.MarkSynced(acDb, "sales_return_summary", "entry_no=@E", new { E = entryNo });
                    outcome.Succeeded++;
                }
                catch (Exception ex)
                {
                    outcome.FailedRefs.Add($"{summary.FormattedEntryNo} — {ex.Message}");
                }
            }

            return outcome;
        }
    }
}