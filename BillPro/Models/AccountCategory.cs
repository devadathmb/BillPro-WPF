namespace BillPro.Models
{
    public class AccountCategory
    {
        public int CategoryCode { get; set; }
        public string CategoryName { get; set; }
        public string PrimaryGroup { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }
}