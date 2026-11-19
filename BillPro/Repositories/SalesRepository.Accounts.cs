using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public partial class SalesRepository
    {
        // Builds the voucher and posts it. Failures never throw out of Save/
        // Update — they flag accounts_synced = 0 so the startup/manual
        // resync sweep can retry later without blocking the bill save.
        private void PostToAccounts(string prefix, int billNo, string formattedBillNo,
            SalesSummary summary, List<SalesLine> lines, string currentUser)
        {
            var voucher = BuildVoucher(formattedBillNo, summary, lines);

            try
            {
                _financialSync.Post(voucher, currentUser);
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkSynced(acDb, "sales_summary", "prefix=@P AND bill_no=@B",
                        new { P = prefix, B = billNo });
            }
            catch
            {
                using (IDbConnection acDb = DatabaseConfig.GetConnection())
                    AccountsSyncStatus.MarkPending(acDb, "sales_summary", "prefix=@P AND bill_no=@B",
                        new { P = prefix, B = billNo });
            }
        }

        private FinancialVoucher BuildVoucher(string formattedBillNo, SalesSummary summary, List<SalesLine> lines)
        {
            return new FinancialVoucher
            {
                Type = TransactionType.Sales,
                VoucherRef = formattedBillNo,
                EntryDate = summary.BillDate.ToString("yyyy-MM-dd"),
                Lines = lines.GroupBy(l => l.TaxRate).Select(g => new FinancialVoucherLine
                {
                    TaxPercent = (double)g.Key,
                    TaxableValue = (double)g.Sum(l => l.TaxableAmount),
                    TaxAmount = (double)g.Sum(l => l.TaxAmount)
                }).ToList(),
                SettlementLegs = BuildSettlementLegs(summary),
                // Whole-bill discount and round-off — previously never
                // posted anywhere, which left the daybook out of balance
                // by exactly these amounts on any bill that used them.
                DiscountAmount = summary.DiscountAmount,
                RoundOffAmount = summary.RoundOff
            };
        }

        // A sale increases what's owed to us, so Cash/Bank/Customer are all
        // debited here (Sales@X% + Tax Collected@X% are credited by
        // ResolveLedgers for TransactionType.Sales — the two sides balance).
        // Requires a "Cash" ac_head to exist (same convention used for
        // BankHeadCode) — falls back to skipping the cash leg if not found,
        // rather than throwing, so an unconfigured Cash head doesn't block
        // every cash sale from posting.
        //
        // LegType is tagged here (Cash / Bank / Party) so
        // FinancialSyncService can decide grouping: Cash and Bank are
        // summarized into daybook_summary same as the Sales@X%/Tax@X% legs
        // (day + series + ledger), but the Customer leg is a real,
        // individually-reconciled account and always stays one row per
        // bill — same as Purchase's settlement legs.
        private List<SettlementLeg> BuildSettlementLegs(SalesSummary summary)
        {
            var legs = new List<SettlementLeg>();

            if (summary.ToCashAmount != 0)
            {
                int cashHeadId = GetOrCreateCashHeadId();
                if (cashHeadId != 0)
                    legs.Add(new SettlementLeg
                    {
                        LedgerId = cashHeadId,
                        DebitAmount = summary.ToCashAmount,
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
                        DebitAmount = summary.ToBankAmount,
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
                        DebitAmount = summary.ToCreditAmount,
                        LegType = SettlementLegType.Party
                    });
            }

            return legs;
        }

        // Delegates to TaxLedgerRepository's canonical Cash-head lookup,
        // which actually CREATES the head (reserved code "1") when missing.
        // The old body here only ever SELECTed and returned 0 on a miss —
        // silently dropping the whole Cash settlement leg for that voucher
        // any time the "Cash" head hadn't been created yet, which is why
        // cash-in-hand could look like it was stuck on the opening balance.
        private int GetOrCreateCashHeadId() => new TaxLedgerRepository().GetOrCreateCashHeadId();

        private int GetAcHeadIdByCode(string acHeadCode)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var id = db.QueryFirstOrDefault<int?>(
                    "SELECT id FROM ac_head_master WHERE ac_head_code = @Code", new { Code = acHeadCode });
                return id ?? 0;
            }
        }

        // Category ID every vendor/customer head is created under — must
        // match VendorCustomerRepository.VcAcCategoryId. Guards the two-hop
        // lookup below: ac_head_code is a global sequence shared by every
        // head type (banks, tax heads, manually-created expense heads...),
        // so if a bare customer_code is ever passed straight into a generic
        // ac_head_code lookup (skipping the vendor_customer_master hop), it
        // can still find a match purely by coincidence — this filter turns
        // that silent-wrong-match into a safe "not found" instead.
        private const int VcAcCategoryId = 3;

        private int GetPartyAcHeadIdByCode(string acHeadCode)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var id = db.QueryFirstOrDefault<int?>(
                    "SELECT id FROM ac_head_master WHERE ac_head_code = @Code AND ac_category_id = @Cat",
                    new { Code = acHeadCode, Cat = VcAcCategoryId });
                return id ?? 0;
            }
        }

        // summary.CustomerCode is vendor_customer_master.customer_code — the
        // customer's own PK (VendorCustomerRepository.GetNextCustomerCode()) —
        // NOT an ac_head_code. Those are two separate sequences (ac_head_code
        // comes from AccountHeadRepository.GetNextAcHeadCode(), shared across
        // every head type) that only happen to coincide for the very first
        // row. The real link is vendor_customer_master.ac_head_code, so
        // resolve customer_code -> ac_head_code (inventory.db) first, then
        // ac_head_code -> the real ac_head_master.id (accounts.db) as before.
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
        /// Retries accounts posting for every bill flagged accounts_synced = 0,
        /// optionally scoped to a bill-date range (both null = every pending
        /// bill, the startup-sweep default).
        ///
        /// forceAll: when true, ignores the accounts_synced flag entirely and
        /// reposts every bill in the date range regardless of its current
        /// flag value. Needed once for data created before this accounts-sync
        /// feature existed — the ADD COLUMN ... DEFAULT 1 migration stamped
        /// every pre-existing row as "synced" even though none of them were
        /// ever actually posted to accounts.db. A date range is effectively
        /// required with forceAll (an unbounded forceAll would re-post the
        /// entire bill history every time it's run).
        /// </summary>
        public ResyncOutcome ResyncPending(string currentUser, DateTime? fromDate = null, DateTime? toDate = null, bool forceAll = false)
        {
            var outcome = new ResyncOutcome();
            List<(string Prefix, int BillNo)> pending;

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = forceAll
                    ? "SELECT prefix AS Prefix, bill_no AS BillNo FROM sales_summary WHERE 1=1"
                    : "SELECT prefix AS Prefix, bill_no AS BillNo FROM sales_summary WHERE accounts_synced = 0";
                if (fromDate.HasValue) sql += " AND bill_date >= @From";
                if (toDate.HasValue) sql += " AND bill_date <= @To";

                pending = db.Query<(string Prefix, int BillNo)>(sql,
                    new { From = fromDate?.ToString("yyyy-MM-dd"), To = toDate?.ToString("yyyy-MM-dd") }).AsList();
            }
            outcome.Pending = pending.Count;

            foreach (var (prefix, billNo) in pending)
            {
                var summary = GetSummary(prefix, billNo);
                if (summary == null) continue;
                var lines = GetItems(prefix, billNo)
                    .Select(i => new SalesLine
                    {
                        TaxRate = (decimal)i.TaxPercent,
                        TaxableAmount = (decimal)i.TaxableAmount,
                        TaxAmount = (decimal)i.TaxAmount
                    }).ToList();

                try
                {
                    if (summary.IsCancelled == 1)
                        _financialSync.Reverse(TransactionType.Sales, summary.FormattedBillNo,
                            summary.BillDate.ToString("yyyy-MM-dd"));
                    else
                        _financialSync.Post(BuildVoucher(summary.FormattedBillNo, summary, lines), currentUser);

                    using (IDbConnection acDb = DatabaseConfig.GetConnection())
                        AccountsSyncStatus.MarkSynced(acDb, "sales_summary", "prefix=@P AND bill_no=@B",
                            new { P = prefix, B = billNo });
                    outcome.Succeeded++;
                }
                catch (Exception ex)
                {
                    outcome.FailedRefs.Add($"{summary.FormattedBillNo} — {ex.Message}");
                }
            }

            return outcome;
        }
    }
}