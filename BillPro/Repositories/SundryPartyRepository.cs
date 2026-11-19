using System;
using System.Collections.Generic;
using System.Linq;
using BillPro.Models;

namespace BillPro.Repositories
{
    /// <summary>
    /// Backs the Sundry Debtors and Sundry Creditors reports — every
    /// vendor/customer a/c head with a non-zero balance as on a given date,
    /// split by which SIDE the balance sits on, not by whether the head was
    /// originally registered as a "Customer" or a "Vendor".
    ///
    /// A customer who's been overpaid sits in credit; a vendor who's been
    /// overpaid (advance, or a return refund not yet settled) sits in
    /// debit. Sundry Debtors/Creditors is a balance-sheet classification,
    /// not a party-type one, so both reports draw from the SAME pool of
    /// heads (every ac_category_id == VcAcCategoryId head — see
    /// VendorCustomerRepository.VcAcCategoryId) and just look at the sign
    /// of each head's balance as on the given date.
    ///
    /// Sign convention follows RunningBalance as produced by
    /// TaxLedgerRepository.GetLedgerHistory: positive = Debit, negative =
    /// Credit — the same convention LedgerReportView already relies on.
    /// Worth a quick sanity check against a known balance the first time
    /// this runs.
    /// </summary>
    public class SundryPartyRepository
    {
        // Must match VendorCustomerRepository.VcAcCategoryId — every
        // vendor/customer a/c head is created under this category, and
        // nothing else is, so filtering on it alone already scopes to
        // exactly the heads either report should ever consider.
        private const int VcAcCategoryId = 3;

        private readonly AccountHeadRepository _acHeadRepo = new();
        private readonly TaxLedgerRepository _taxLedgerRepo = new();

        /// <summary>
        /// Sundry Debtors — every vendor/customer head with a positive
        /// (Debit) balance as on <paramref name="asOnDate"/>.
        /// </summary>
        public List<SundryPartyReportRow> GetDebtors(DateTime asOnDate) =>
            GetPartyBalances(asOnDate, wantDebit: true);

        /// <summary>
        /// Sundry Creditors — every vendor/customer head with a negative
        /// (Credit) balance as on <paramref name="asOnDate"/>.
        /// </summary>
        public List<SundryPartyReportRow> GetCreditors(DateTime asOnDate) =>
            GetPartyBalances(asOnDate, wantDebit: false);

        private List<SundryPartyReportRow> GetPartyBalances(DateTime asOnDate, bool wantDebit)
        {
            var heads = _acHeadRepo.GetHeadsByCategory(VcAcCategoryId);
            var rows = new List<SundryPartyReportRow>();

            foreach (var head in heads)
            {
                var history = _taxLedgerRepo.GetLedgerHistory(head.Id);

                double balance = history
                    .Where(h => h.EntryDateValue.Date <= asOnDate.Date)
                    .Select(h => (double?)h.RunningBalance)
                    .LastOrDefault() ?? 0;

                if (balance == 0) continue;
                if (wantDebit && balance < 0) continue;
                if (!wantDebit && balance > 0) continue;

                rows.Add(new SundryPartyReportRow
                {
                    LedgerId = head.Id,
                    AcHeadCode = head.AcHeadCode,
                    AcHeadName = head.AcHeadName,
                    Balance = Math.Abs(balance)
                });
            }

            return rows.OrderBy(r => r.AcHeadName).ToList();
        }
    }
}