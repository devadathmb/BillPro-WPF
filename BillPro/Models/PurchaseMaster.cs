using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using BillPro.Helpers;

namespace BillPro.Models
{
    // ══════════════════════════════════════════════════════════════
    // PURCHASE SUMMARY (Header / Master record)
    // ══════════════════════════════════════════════════════════════
    public class PurchaseSummary : ObservableObject
    {
        private int _id;
        private int _purchaseNo;
        private DateTime _purchaseDate = DateTime.Today;
        private string _billNo = "";
        private DateTime _billDate = DateTime.Today;
        private int _purchaseMode;          // 0 = Cash, 1 = Credit
        private int _vendorCode;
        private string _vendorName = "";
        private string _address1 = "";
        private string _address2 = "";
        private string _address3 = "";
        private string _gstNo = "";
        private string _phone = "";
        private string _mobile = "";
        private string _state = "";
        private string _stateCode = "";
        private decimal _cashDiscount;
        private decimal _roundedAmount;
        private string _createdBy = "";
        private DateTime _createdDate = DateTime.Now;
        private string _lastModifiedBy = "";
        private DateTime? _lastModifiedDate;

        // ── Money-transfer / settlement details ──────────────────────────────
        // How the purchase amount was actually settled: partly to a bank
        // account (card/transfer), partly cash, and the remainder left owing
        // to the vendor as a creditor balance (daybook posting comes later).
        private decimal _toCardAmount;
        private int _bankHeadCode;
        private string _bankHeadName = "";
        private decimal _toCashAmount;
        private decimal _toCreditAmount;

        public int Id { get => _id; set => SetProperty(ref _id, value); }
        public int PurchaseNo { get => _purchaseNo; set => SetProperty(ref _purchaseNo, value); }
        public DateTime PurchaseDate { get => _purchaseDate; set => SetProperty(ref _purchaseDate, value); }
        public string BillNo { get => _billNo; set => SetProperty(ref _billNo, value); }
        public DateTime BillDate { get => _billDate; set => SetProperty(ref _billDate, value); }
        public int PurchaseMode { get => _purchaseMode; set => SetProperty(ref _purchaseMode, value); }
        public int VendorCode { get => _vendorCode; set => SetProperty(ref _vendorCode, value); }
        public string VendorName { get => _vendorName; set => SetProperty(ref _vendorName, value); }
        public string Address1 { get => _address1; set => SetProperty(ref _address1, value); }
        public string Address2 { get => _address2; set => SetProperty(ref _address2, value); }
        public string Address3 { get => _address3; set => SetProperty(ref _address3, value); }
        public string GstNo { get => _gstNo; set => SetProperty(ref _gstNo, value); }
        public string Phone { get => _phone; set => SetProperty(ref _phone, value); }
        public string Mobile { get => _mobile; set => SetProperty(ref _mobile, value); }
        public string State { get => _state; set => SetProperty(ref _state, value); }
        public string StateCode { get => _stateCode; set => SetProperty(ref _stateCode, value); }
        public decimal CashDiscount { get => _cashDiscount; set => SetProperty(ref _cashDiscount, value); }
        public decimal RoundedAmount { get => _roundedAmount; set => SetProperty(ref _roundedAmount, value); }
        public string CreatedBy { get => _createdBy; set => SetProperty(ref _createdBy, value); }
        public DateTime CreatedDate { get => _createdDate; set => SetProperty(ref _createdDate, value); }
        public string LastModifiedBy { get => _lastModifiedBy; set => SetProperty(ref _lastModifiedBy, value); }
        public DateTime? LastModifiedDate { get => _lastModifiedDate; set => SetProperty(ref _lastModifiedDate, value); }

        public decimal ToCardAmount { get => _toCardAmount; set => SetProperty(ref _toCardAmount, value); }
        public int BankHeadCode { get => _bankHeadCode; set => SetProperty(ref _bankHeadCode, value); }
        public string BankHeadName { get => _bankHeadName; set => SetProperty(ref _bankHeadName, value); }
        public decimal ToCashAmount { get => _toCashAmount; set => SetProperty(ref _toCashAmount, value); }
        public decimal ToCreditAmount { get => _toCreditAmount; set => SetProperty(ref _toCreditAmount, value); }

        // ── Computed totals (populated from child collections) ──────────────────
        private decimal _priceAmount;
        private decimal _taxTotal;
        private decimal _grandTotal;
        private decimal _netTotal;

        public decimal PriceAmount { get => _priceAmount; set => SetProperty(ref _priceAmount, value); }
        public decimal TaxTotal { get => _taxTotal; set => SetProperty(ref _taxTotal, value); }
        public decimal GrandTotal { get => _grandTotal; set => SetProperty(ref _grandTotal, value); }
        public decimal NetTotal { get => _netTotal; set => SetProperty(ref _netTotal, value); }
    }

    // Report row models (PurchaseReportRow, PurchaseItemReportRow,
    // PurchaseItemSummaryRow, PurchaseSettlementReportRow) moved to
    // PurchaseReportModels.cs, mirroring SalesReportModels.cs.

    // PurchaseItemSummaryRow, PurchaseSettlementReportRow moved to
    // PurchaseReportModels.cs.

    // ══════════════════════════════════════════════════════════════
    // PURCHASE ITEM DETAIL (Line items grid)
    // ══════════════════════════════════════════════════════════════
    public class PurchaseItemDetail : ObservableObject
    {
        private int _id;
        private int _purchaseNo;
        private DateTime _purchaseDate = DateTime.Today;
        private string _itemCode = "";
        private string _itemName = "";
        private string _hsnCode = "";
        private decimal _mrp;
        private decimal _rate;
        private decimal _discountRate; // NEW: Discount Percent
        private decimal _discount;     // Amount
        private decimal _quantity;
        private decimal _taxRate;
        private decimal _taxAmount;
        private decimal _price;        // Amount before tax
        private decimal _total;        // Amount + tax
        private decimal _landingCost;
        private decimal _landingCostStock;

        // Links this line to the batch/ledger row it created in the
        // Master+Batch+Ledger stock system (see StockMasterRepository).
        // Populated by the repository after Save/Update; not user-editable.
        public int? BatchId { get; set; }
        public int? TransId { get; set; }

        // Flag to prevent recursive stack overflows during calculations
        private bool _isCalculating;

        public int Id { get => _id; set => SetProperty(ref _id, value); }
        public int PurchaseNo { get => _purchaseNo; set => SetProperty(ref _purchaseNo, value); }
        public DateTime PurchaseDate { get => _purchaseDate; set => SetProperty(ref _purchaseDate, value); }
        public string ItemCode { get => _itemCode; set => SetProperty(ref _itemCode, value); }
        public string HsnCode { get => _hsnCode; set => SetProperty(ref _hsnCode, value); }
        public string ItemName { get => _itemName; set => SetProperty(ref _itemName, value); }
        public decimal Mrp { get => _mrp; set => SetProperty(ref _mrp, value); }
        public decimal LandingCost { get => _landingCost; set => SetProperty(ref _landingCost, value); }
        public decimal LandingCostStock { get => _landingCostStock; set => SetProperty(ref _landingCostStock, value); }
        public decimal Total { get => _total; set => SetProperty(ref _total, value); } // Locked, computed only

        // ── Editable Fields with Cascading Calculations ─────────────────────────

        public decimal Rate
        {
            get => _rate;
            set { if (SetProperty(ref _rate, value)) CalculateFromRate(); }
        }

        public decimal Quantity
        {
            get => _quantity;
            set { if (SetProperty(ref _quantity, value)) CalculateFromRate(); }
        }

        public decimal DiscountRate
        {
            get => _discountRate;
            set { if (SetProperty(ref _discountRate, value)) CalculateFromRate(); }
        }

        public decimal Discount
        {
            get => _discount;
            set { if (SetProperty(ref _discount, value)) CalculateFromDiscount(); }
        }

        public decimal Price
        {
            get => _price;
            set { if (SetProperty(ref _price, value)) CalculateFromPrice(); }
        }

        public decimal TaxRate
        {
            get => _taxRate;
            set { if (SetProperty(ref _taxRate, value)) CalculateFromTaxRate(); }
        }

        public decimal TaxAmount
        {
            get => _taxAmount;
            set { if (SetProperty(ref _taxAmount, value)) CalculateTotalOnly(); }
        }

        private void CalculateFromRate()
        {
            if (_isCalculating) return;
            _isCalculating = true;

            decimal gross = Math.Round(_rate * _quantity, 2);

            // Calculate Discount Amount based on Rate
            _discount = Math.Round(gross * (_discountRate / 100m), 2);
            OnPropertyChanged(nameof(Discount));

            _price = gross - _discount;
            OnPropertyChanged(nameof(Price));

            _taxAmount = Math.Round(_price * (_taxRate / 100m), 2);
            OnPropertyChanged(nameof(TaxAmount));

            _total = _price + _taxAmount;
            OnPropertyChanged(nameof(Total));

            _isCalculating = false;
        }

        private void CalculateFromDiscount()
        {
            if (_isCalculating) return;
            _isCalculating = true;

            // If user manually changes Discount amount, just adjust Price down the line
            decimal gross = Math.Round(_rate * _quantity, 2);

            _price = gross - _discount;
            OnPropertyChanged(nameof(Price));

            _taxAmount = Math.Round(_price * (_taxRate / 100m), 2);
            OnPropertyChanged(nameof(TaxAmount));

            _total = _price + _taxAmount;
            OnPropertyChanged(nameof(Total));

            _isCalculating = false;
        }

        private void CalculateFromPrice()
        {
            if (_isCalculating) return;
            _isCalculating = true;

            // If user manually changes Price, recalculate Tax and Total based on new Price
            _taxAmount = Math.Round(_price * (_taxRate / 100m), 2);
            OnPropertyChanged(nameof(TaxAmount));

            _total = _price + _taxAmount;
            OnPropertyChanged(nameof(Total));

            _isCalculating = false;
        }

        private void CalculateFromTaxRate()
        {
            if (_isCalculating) return;
            _isCalculating = true;

            _taxAmount = Math.Round(_price * (_taxRate / 100m), 2);
            OnPropertyChanged(nameof(TaxAmount));

            _total = _price + _taxAmount;
            OnPropertyChanged(nameof(Total));

            _isCalculating = false;
        }

        private void CalculateTotalOnly()
        {
            if (_isCalculating) return;
            _isCalculating = true;

            // If user manually overrides TaxAmount, just update the Total
            _total = _price + _taxAmount;
            OnPropertyChanged(nameof(Total));

            _isCalculating = false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // PURCHASE EXTERNAL EXPENSE (Freight / A/c head details grid)
    // ══════════════════════════════════════════════════════════════
    public class PurchaseExternalExpense : ObservableObject
    {
        private int _id;
        private int _purchaseNo;
        private DateTime _purchaseDate = DateTime.Today;
        private int _acHeadCode;
        private string _acHeadName = "";
        private string _narration = "";
        private decimal _credit;
        private decimal _debit;
        private decimal _externalTaxRate;
        private decimal _externalTax;

        public int Id { get => _id; set => SetProperty(ref _id, value); }
        public int PurchaseNo { get => _purchaseNo; set => SetProperty(ref _purchaseNo, value); }
        public DateTime PurchaseDate { get => _purchaseDate; set => SetProperty(ref _purchaseDate, value); }
        public int AcHeadCode { get => _acHeadCode; set => SetProperty(ref _acHeadCode, value); }
        public string AcHeadName { get => _acHeadName; set => SetProperty(ref _acHeadName, value); }
        public string Narration { get => _narration; set => SetProperty(ref _narration, value); }

        public decimal Credit
        {
            get => _credit;
            set { if (SetProperty(ref _credit, value)) RecalcTax(); }
        }

        public decimal Debit
        {
            get => _debit;
            set { if (SetProperty(ref _debit, value)) RecalcTax(); }
        }

        public decimal ExternalTaxRate
        {
            get => _externalTaxRate;
            set { if (SetProperty(ref _externalTaxRate, value)) RecalcTax(); }
        }

        public decimal ExternalTax { get => _externalTax; set => SetProperty(ref _externalTax, value); }

        private void RecalcTax()
        {
            decimal base_ = _debit != 0 ? _debit : _credit;
            ExternalTax = Math.Round(base_ * _externalTaxRate / 100m, 2);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // VENDOR / CUSTOMER (for the Credit purchase SmartComboBox)
    // ══════════════════════════════════════════════════════════════
    public class VendorCustomer
    {
        public int CustomerCode { get; set; }
        public string CustomerName { get; set; }
        public string Address1 { get; set; }
        public string Address2 { get; set; }
        public string Address3 { get; set; }
        public string GstNo { get; set; }
        public string Phone { get; set; }
        public string Mobile { get; set; }
        public int StateId { get; set; }
        public string Category { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // ACCOUNT HEAD LOOKUP (for expenses grid SmartComboBox)
    // ══════════════════════════════════════════════════════════════
    public class AcHeadLookup
    {
        public int AcHeadCode { get; set; }
        public string AcHeadName { get; set; }
        public string Category { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // ITEM LOOKUP (for items grid SmartComboBox — multi-column)
    // ══════════════════════════════════════════════════════════════
    public class ItemLookup
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public decimal Mrp { get; set; }
        public decimal UnitPricePurchase { get; set; }
        public decimal UnitPriceSales { get; set; }
        public decimal TaxRate { get; set; }
        public decimal AverageCost { get; set; }
    }
}