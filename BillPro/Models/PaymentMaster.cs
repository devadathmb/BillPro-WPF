namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // A-C HEAD PAYMENT ENTRY
    // Same shape as ReceiptEntry minus CashDiscount (payments don't carry
    // a cash-discount concept the way customer receipts do).
    // ─────────────────────────────────────────────────────────────────────
    public class PaymentEntry
    {
        public int PaymentNo { get; set; }
        public string PaymentDate { get; set; }        // stored as ISO "yyyy-MM-dd"

        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }

        public string Narration { get; set; }
        public decimal PaymentAmount { get; set; }

        public string PaymentMode { get; set; }         // "Cash" or "Bank"
        public string BankAcHeadCode { get; set; }
        public string BankAcHeadName { get; set; }
        public string BankNarration { get; set; }

        public decimal LedgerBalance { get; set; }

        public bool CancelStatus { get; set; }

        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }
}