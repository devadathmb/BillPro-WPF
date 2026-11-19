// PurchaseReturnMaster.cs
using System;
using System.ComponentModel;

namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // PURCHASE RETURN — decreases stock (goods sent back to vendor).
    // Mirrors SalesReturnSummary's shape, vendor-side instead of customer-side.
    // ─────────────────────────────────────────────────────────────────────
    public class PurchaseReturnSummary
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }         // "PR-123"
        public DateTime EntryDate { get; set; }

        public string OriginalBillNo { get; set; }            // vendor's purchase bill no being returned against

        public string BillType { get; set; }                  // "Cash" / "Credit"
        public string VendorCode { get; set; }
        public string VendorName { get; set; }
        public string VendorAddress { get; set; }
        public string VendorPhone { get; set; }
        public string VendorGstNo { get; set; }
        public string State { get; set; }
        public string StateCode { get; set; }

        public double GrossAmount { get; set; }
        public double DiscountAmount { get; set; }
        public double DiscountPercent { get; set; }
        public double TaxAmount { get; set; }
        public double RoundOff { get; set; }
        public double NetTotal { get; set; }

        public double ToCashAmount { get; set; }
        public double ToBankAmount { get; set; }
        public int BankHeadCode { get; set; }
        public string BankHeadName { get; set; }
        public double ToCreditAmount { get; set; }             // adjusted against vendor's payable balance

        public int IsCancelled { get; set; }

        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }

    public class PurchaseReturnItemDetail
    {
        public int EntryNo { get; set; }
        public int SlNo { get; set; }

        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public int BatchId { get; set; }                        // which batch stock is deducted from
        public string BatchNumber { get; set; }
        public int TransId { get; set; }                         // the PUR_RETURN ledger row (qty_out)

        public double Qty { get; set; }
        public double Rate { get; set; }
        public double DiscountPercent { get; set; }
        public double DiscountAmount { get; set; }
        public double TaxableAmount { get; set; }
        public double TaxPercent { get; set; }
        public double TaxAmount { get; set; }
        public double TotalAmount { get; set; }
    }

    public class PurchaseReturnLine : INotifyPropertyChanged
    {
        private string _itemCode, _itemName, _batchNumber;
        private int _batchId;
        private decimal _rate, _quantity, _discountRate, _discount, _taxRate, _taxAmount, _taxableAmount, _total;
        private bool _isInsufficientStock;

        public int TransId { get; set; }

        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(nameof(ItemCode)); } }
        public string ItemName { get => _itemName; set { _itemName = value; OnChanged(nameof(ItemName)); } }
        public int BatchId { get => _batchId; set { _batchId = value; OnChanged(nameof(BatchId)); } }
        public string BatchNumber { get => _batchNumber; set { _batchNumber = value; OnChanged(nameof(BatchNumber)); } }
        public decimal Rate { get => _rate; set { _rate = value; OnChanged(nameof(Rate)); } }
        public decimal Quantity { get => _quantity; set { _quantity = value; OnChanged(nameof(Quantity)); } }
        public decimal DiscountRate { get => _discountRate; set { _discountRate = value; OnChanged(nameof(DiscountRate)); } }
        public decimal Discount { get => _discount; set { _discount = value; OnChanged(nameof(Discount)); } }
        public decimal TaxRate { get => _taxRate; set { _taxRate = value; OnChanged(nameof(TaxRate)); } }
        public decimal TaxAmount { get => _taxAmount; set { _taxAmount = value; OnChanged(nameof(TaxAmount)); } }
        public decimal TaxableAmount { get => _taxableAmount; set { _taxableAmount = value; OnChanged(nameof(TaxableAmount)); } }
        public decimal Total { get => _total; set { _total = value; OnChanged(nameof(Total)); } }

        // Purchase return DECREASES stock — flagged when the return qty
        // exceeds what's currently in that batch (mirrors the insufficient-
        // stock confirm-dialog pattern from Sales Entry, just the reverse
        // direction). Purely a UI warning; doesn't block the entry line.
        public bool IsInsufficientStock { get => _isInsufficientStock; set { _isInsufficientStock = value; OnChanged(nameof(IsInsufficientStock)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Batch lookup for the entry combo — only real, existing batches
    // (a purchase return obviously can't return stock that was never
    // purchased), no synthetic "(New Batch)" row.
    public class PurchaseReturnItemLookup
    {
        public int BatchId { get; set; }
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public string BatchNumber { get; set; }
        public double Mrp { get; set; }
        public double PurchaseRate { get; set; }
        public double TaxRate { get; set; }
        public double CurrentStock { get; set; }
    }
}