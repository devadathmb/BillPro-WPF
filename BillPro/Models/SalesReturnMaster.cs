// SalesReturnMaster.cs
using System;
using System.ComponentModel;

namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // SALES RETURN — increases stock (customer sends goods back).
    // No series/prefix system — a single global auto-increment entry number
    // ("SR-1", "SR-2", ...), separate from sales_series_master entirely.
    // Otherwise mirrors SalesSummary's bill-like shape (customer/GST/tax/
    // settlement) since returns can involve a refund split same as a bill.
    // ─────────────────────────────────────────────────────────────────────
    public class SalesReturnSummary
    {
        public int EntryNo { get; set; }                  // PK, raw autoincrement
        public string FormattedEntryNo { get; set; }        // "SR-123", display/print only
        public DateTime EntryDate { get; set; }

        // The original sales bill this return is against. Free text, not a
        // strict FK — legacy/deleted bills must still be enterable — but
        // validated against sales_summary.formatted_bill_no when possible
        // (see SalesReturnRepository.ValidateOriginalBillNo).
        public string OriginalBillNo { get; set; }

        public string BillType { get; set; }               // "Cash" / "Credit"
        public string CustomerCode { get; set; }
        public string CustomerName { get; set; }
        public string CustomerAddress { get; set; }
        public string CustomerPhone { get; set; }
        public string CustomerGstNo { get; set; }
        public string State { get; set; }
        public string StateCode { get; set; }

        public double GrossAmount { get; set; }
        public double DiscountAmount { get; set; }
        public double DiscountPercent { get; set; }
        public double TaxAmount { get; set; }
        public double RoundOff { get; set; }
        public double NetTotal { get; set; }

        // Settlement — how the refund was actually paid out.
        public double ToCashAmount { get; set; }
        public double ToBankAmount { get; set; }
        public int BankHeadCode { get; set; }
        public string BankHeadName { get; set; }
        public double ToCreditAmount { get; set; }         // adjusted against customer's credit balance instead of paid out

        public int IsCancelled { get; set; }

        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }

    public class SalesReturnItemDetail
    {
        public int EntryNo { get; set; }                    // PK part 1
        public int SlNo { get; set; }                        // PK part 2

        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public int BatchId { get; set; }                     // which batch stock is added back to
        public string BatchNumber { get; set; }
        public int TransId { get; set; }                     // the SALE_RETURN ledger row (qty_in)

        public double Qty { get; set; }
        public double Rate { get; set; }

        // Ex-tax per-unit price, backed out of the tax-inclusive Rate
        // (Rate * 100/(100+TaxPercent)) — same convention as
        // SalesItemDetail.UnitPrice. Persisted so a reopened return shows
        // exactly what it was calculated at. 0 for rows saved before this
        // column existed.
        public double UnitPrice { get; set; }
        public double DiscountPercent { get; set; }
        public double DiscountAmount { get; set; }
        public double TaxableAmount { get; set; }
        public double TaxPercent { get; set; }
        public double TaxAmount { get; set; }
        public double TotalAmount { get; set; }
    }

    // Grid row model — same conventions as SalesLine (decimal, INotifyPropertyChanged)
    public class SalesReturnLine : INotifyPropertyChanged
    {
        private string _itemCode, _itemName, _batchNumber;
        private int _batchId;
        private decimal _rate, _unitPrice, _quantity, _discountRate, _discount, _taxRate, _taxAmount, _taxableAmount, _total;

        public int TransId { get; set; }

        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(nameof(ItemCode)); } }
        public string ItemName { get => _itemName; set { _itemName = value; OnChanged(nameof(ItemName)); } }
        public int BatchId { get => _batchId; set { _batchId = value; OnChanged(nameof(BatchId)); } }
        public string BatchNumber { get => _batchNumber; set { _batchNumber = value; OnChanged(nameof(BatchNumber)); } }
        public decimal Rate { get => _rate; set { _rate = value; OnChanged(nameof(Rate)); } }

        // Ex-tax per-unit price, derived from Rate/TaxRate — see
        // SalesLine.UnitPrice for the full rationale. Kept unrounded;
        // the grid formats it to 2dp for display only.
        public decimal UnitPrice { get => _unitPrice; set { _unitPrice = value; OnChanged(nameof(UnitPrice)); } }
        public decimal Quantity { get => _quantity; set { _quantity = value; OnChanged(nameof(Quantity)); } }
        public decimal DiscountRate { get => _discountRate; set { _discountRate = value; OnChanged(nameof(DiscountRate)); } }
        public decimal Discount { get => _discount; set { _discount = value; OnChanged(nameof(Discount)); } }
        public decimal TaxRate { get => _taxRate; set { _taxRate = value; OnChanged(nameof(TaxRate)); } }
        public decimal TaxAmount { get => _taxAmount; set { _taxAmount = value; OnChanged(nameof(TaxAmount)); } }
        public decimal TaxableAmount { get => _taxableAmount; set { _taxableAmount = value; OnChanged(nameof(TaxableAmount)); } }
        public decimal Total { get => _total; set { _total = value; OnChanged(nameof(Total)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Item lookup for the return entry combo — one row PER BATCH, same as
    // SalesItemLookup, EXCEPT there's no "0/negative stock" allowance here:
    // a return always ADDS stock back, so there's no synthetic "(New Batch)"
    // row and no low-stock warning. Only batches that already exist (i.e.
    // were actually purchased/sold before) are selectable.
    public class SalesReturnItemLookup
    {
        public int BatchId { get; set; }
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public string BatchNumber { get; set; }
        public double Mrp { get; set; }
        public double SalesRate { get; set; }
        public double TaxRate { get; set; }
        public double CurrentStock { get; set; }
    }
}