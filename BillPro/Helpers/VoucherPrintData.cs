using System;

namespace BillPro.Helpers
{
    // ─────────────────────────────────────────────────────────────────────
    // Plain data carrier for VoucherPrintHelper — one shape shared by both
    // Receipt and Payment entry screens (Payment just leaves Discount at 0
    // and uses "Paid Amount" as the DescriptionLabel).
    // ─────────────────────────────────────────────────────────────────────
    public class VoucherPrintData
    {
        public string VoucherType { get; set; }      // "RECEIPT" or "PAYMENT"
        public int VoucherNo { get; set; }
        public DateTime VoucherDate { get; set; }

        public string AcHeadName { get; set; }
        public string Address { get; set; }
        public string Mobile { get; set; }
        public string GstNo { get; set; }

        public string DescriptionLabel { get; set; } // "Received Amount" / "Paid Amount"
        public decimal Amount { get; set; }
        public string Narration { get; set; }
        public decimal Discount { get; set; }

        public string Mode { get; set; }              // "Cash" or "Bank"
        public string BankName { get; set; }
        public string BankNarration { get; set; }
    }
}