namespace BillPro.Models
{
    public class ItemMaster
    {
        public string ItemCode { get; set; }
        public string HsnCode { get; set; }
        public string ItemName { get; set; }
        public string ItemCategoryCode { get; set; }
        public string CompanyCode { get; set; }
        public double Mrp { get; set; }
        public double UnitPriceSales { get; set; }
        public double UnitPricePurchase { get; set; }
        public double TaxRate { get; set; }
        public double QtyPouch { get; set; }
        public double PouchBox { get; set; }
        public double ReOrderLevel { get; set; }
        public string Rack { get; set; }
        public string SalesUnit { get; set; }
        public int Category { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
        public int IsActive { get; set; }
    }

    public class CategoryItem
    {
        public string CategoryCode { get; set; }
        public string CategoryName { get; set; }
    }

    public class CompanyItem
    {
        public string CompanyCode { get; set; }
        public string CompanyName { get; set; }
    }
}