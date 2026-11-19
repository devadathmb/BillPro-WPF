namespace BillPro.Models
{
    public class AccountHeadMaster
    {
        public int Id { get; set; }
        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }
        public int AcCategoryId { get; set; }
        public int AcGroupId { get; set; }
        public decimal OpeningCredit { get; set; }
        public decimal OpeningDebit { get; set; }

        // Hidden fields for future Ledger/Vendor sync
        public string Address { get; set; }
        public string Phone { get; set; }
        public string Mobile { get; set; }

        // Added for Receipt/Payment Entry lookup (GSTIN of the a/c head, if any)
        public string GstNo { get; set; }
    }
}