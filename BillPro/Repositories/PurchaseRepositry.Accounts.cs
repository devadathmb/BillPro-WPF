using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public partial class PurchaseRepository
    {
        // Purchase has no formatted-bill-no concept like Sales (BillNo is
        // the vendor's own free-text bill number, not unique/reliable as a
        // key) — PurchaseNo is the real internal PK, so the voucher ref is
        // built from that.
        private string PurchaseVoucherRef(PurchaseSummary summary) => $"PUR-{summary.PurchaseNo}";

        private void PostToAccounts(PurchaseSummary summary, IList<PurchaseItemDetail> items, string currentUser)
        {
            var voucher = BuildVoucher(summary, items);

            try
            {
                _financialSync.Post(voucher, currentUser);
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(acDb, "purchase_summary_details", "purchase_no=@P",
                        new { P = summary.PurchaseNo });
            }
            catch
            {
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(acDb, "purchase_summary_details", "purchase_no=@P",
                        new { P = summary.PurchaseNo });
            }
        }

        private FinancialVoucher BuildVoucher(PurchaseSummary summary, IList<PurchaseItemDetail> items)
        {
            return new FinancialVoucher
            {
                Type = TransactionType.Purchase,
                VoucherRef = PurchaseVoucherRef(summary),
                EntryDate = summary.PurchaseDate.ToString("yyyy-MM-dd"),
                Lines = items.GroupBy(i => i.TaxRate).Select(g => new FinancialVoucherLine
                {
                    TaxPercent = (double)g.Key,
                    TaxableValue = (double)g.Sum(i => i.Price),
                    TaxAmount = (double)g.Sum(i => i.TaxAmount)
                }).ToList(),
                SettlementLegs = BuildSettlementLegs(summary),
                // Purchase has no bill-level DiscountAmount field like Sales/
                // SalesReturn/PurchaseReturn — CashDiscount and RoundedAmount
                // fill that role here instead. Line-level Discount is
                // deliberately excluded: it's already baked into landing
                // cost/average_cost, not a separate ledger contra.
                DiscountAmount = (double)summary.CashDiscount,
                RoundOffAmount = (double)summary.RoundedAmount
            };
        }

        // A purchase increases what WE owe, so Purchase@X% + TaxPaid@X% are
        // debited by ResolveLedgers — meaning the settlement legs here must
        // be CREDITED (cash/bank going out, or the vendor's payable balance
        // going up) to balance the voucher. Mirror image of Sales.
        private List<SettlementLeg> BuildSettlementLegs(PurchaseSummary summary)
        {
            var legs = new List<SettlementLeg>();

            if (summary.ToCashAmount != 0)
            {
                int cashHeadId = GetOrCreateCashHeadId();
                if (cashHeadId != 0)
                    legs.Add(new SettlementLeg { LedgerId = cashHeadId, CreditAmount = (double)summary.ToCashAmount });
            }

            if (summary.ToCardAmount != 0 && summary.BankHeadCode != 0)
            {
                int bankHeadId = GetAcHeadIdByCode(summary.BankHeadCode.ToString());
                if (bankHeadId != 0)
                    legs.Add(new SettlementLeg { LedgerId = bankHeadId, CreditAmount = (double)summary.ToCardAmount });
            }

            if (summary.ToCreditAmount != 0 && summary.VendorCode != 0)
            {
                int vendorHeadId = GetAcHeadIdForParty(summary.VendorCode.ToString());
                if (vendorHeadId != 0)
                    legs.Add(new SettlementLeg { LedgerId = vendorHeadId, CreditAmount = (double)summary.ToCreditAmount });
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
        // two-hop lookup (vendor_code -> ac_head_code -> ac_head_master.id)
        // is needed instead of treating VendorCode as an ac_head_code.
        private int GetAcHeadIdForParty(string vendorCode)
        {
            if (string.IsNullOrEmpty(vendorCode)) return 0;

            string acHeadCode;
            using (IDbConnection db = DatabaseConfig.GetConnection())
                acHeadCode = db.QueryFirstOrDefault<string>(
                    "SELECT ac_head_code FROM vendor_customer_master WHERE customer_code = @Code",
                    new { Code = vendorCode });

            return string.IsNullOrEmpty(acHeadCode) ? 0 : GetPartyAcHeadIdByCode(acHeadCode);
        }

        /// <summary>
        /// Retries accounts posting for every purchase flagged
        /// accounts_synced = 0, optionally scoped to a purchase-date range
        /// (both null = every pending row, the startup-sweep default).
        /// Purchase never soft-deletes (Delete is a hard delete), so unlike
        /// Sales there's no "cancelled" branch here — a pending row is
        /// always a Post retry, never a Reverse retry.
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
                    ? "SELECT purchase_no FROM purchase_summary_details WHERE 1=1"
                    : "SELECT purchase_no FROM purchase_summary_details WHERE accounts_synced = 0";
                if (fromDate.HasValue) sql += " AND purchase_date >= @From";
                if (toDate.HasValue) sql += " AND purchase_date <= @To";

                pending = db.Query<int>(sql,
                    new { From = fromDate?.ToString("yyyy-MM-dd"), To = toDate?.ToString("yyyy-MM-dd") }).AsList();
            }
            outcome.Pending = pending.Count;

            foreach (var purchaseNo in pending)
            {
                var summary = GetSummaryByNo(purchaseNo);
                if (summary == null) continue;
                var items = GetItemsByPurchaseNo(purchaseNo);

                try
                {
                    _financialSync.Post(BuildVoucher(summary, items), currentUser);
                    using (IDbConnection acDb = DatabaseConfig.GetConnection())
                        AccountsSyncStatus.MarkSynced(acDb, "purchase_summary_details", "purchase_no=@P",
                            new { P = purchaseNo });
                    outcome.Succeeded++;
                }
                catch (Exception ex)
                {
                    outcome.FailedRefs.Add($"{PurchaseVoucherRef(summary)} — {ex.Message}");
                }
            }

            return outcome;
        }
    }
}