namespace BillPro.Models
{
    public class HSNCodeMaster
    {
        public int Id { get; set; } // Renamed back to Id
        public string HsnCode { get; set; }
        public string ItemDescription { get; set; }
        public decimal TaxRate { get; set; }
    }
}