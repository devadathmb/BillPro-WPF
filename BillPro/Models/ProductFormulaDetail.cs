namespace BillPro.Models
{
    public class ProductFormulaDetail
    {
        public int Id { get; set; }
        public string FinishedGoodCode { get; set; } = "";
        public string RawMaterialCode { get; set; } = "";
        public string ItemName { get; set; } = "";
        public double Quantity { get; set; }
    }
}