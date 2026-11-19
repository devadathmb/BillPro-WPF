// SundryPartyReport.cs
namespace BillPro.Models
{
    /// <summary>
    /// One row of the Sundry Debtors / Sundry Creditors report — a single
    /// vendor/customer a/c head with a non-zero balance as on the selected
    /// date. See SundryPartyRepository.GetDebtors / GetCreditors.
    ///
    /// Balance is always the positive magnitude — which side it's on is
    /// implied by which report it came from (Debtors = Debit, Creditors =
    /// Credit), so there's no separate Dr/Cr column; each view just labels
    /// this "Debit" or "Credit" as appropriate.
    /// </summary>
    public class SundryPartyReportRow
    {
        public int LedgerId { get; set; }        // ac_head_master.id — used to jump to the Ledger Report
        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }
        public double Balance { get; set; }
    }
}