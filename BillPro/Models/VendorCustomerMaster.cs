namespace BillPro.Models
{
    public class VendorCustomerMaster
    {
        public string CustomerCode { get; set; }
        public string CustomerName { get; set; }
        public string Address1 { get; set; }
        public string Address2 { get; set; }
        public string Address3 { get; set; }
        public string Place { get; set; }
        public string Pin { get; set; }
        public string Phone { get; set; }
        public string Mobile { get; set; }
        public string Fax { get; set; }
        public string Email { get; set; }
        public string CstNo { get; set; }
        public string TinNo { get; set; }
        public string GstNo { get; set; }
        public string ContactPerson { get; set; }
        public string Category { get; set; }
        public string Is194Q { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
        public string AcHeadCode {get; set; }

        public override string ToString()
        {
            return CustomerName ?? "";
        }
    }
}