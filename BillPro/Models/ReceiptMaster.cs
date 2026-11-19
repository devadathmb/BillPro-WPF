namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // CUSTOMER / A-C HEAD RECEIPT ENTRY
    // Purely an accounts.db entity — records money received against any
    // ac_head (customer, cash, bank, etc). Mirrors the VB6 customer receipt
    // form: cash mode by default, or Bank mode where the amount is
    // deposited to a bank a/c head with a supporting narration.
    // ─────────────────────────────────────────────────────────────────────
    public class ReceiptEntry
    {
        public int ReceiptNo { get; set; }
        public string ReceiptDate { get; set; }       // stored as ISO "yyyy-MM-dd"

        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }         // denormalised for quick listing/printing

        public string Narration { get; set; }
        public decimal ReceiptAmount { get; set; }
        public decimal CashDiscount { get; set; }

        public string ReceiptMode { get; set; }        // "Cash" or "Bank"
        public string BankAcHeadCode { get; set; }      // ac_head_master code of the bank (Bank mode only)
        public string BankAcHeadName { get; set; }
        public string BankNarration { get; set; }

        public decimal LedgerBalance { get; set; }      // snapshot at time of entry

        public bool CancelStatus { get; set; }

        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }
}