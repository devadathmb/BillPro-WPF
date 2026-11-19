using System;
using System.ComponentModel;

namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // SERIES CONFIGURATION  (System section — one row per registered prefix)
    // ─────────────────────────────────────────────────────────────────────
    public class SalesSeriesMaster
    {
        public string Prefix { get; set; }              // PK, e.g. "GA"
        public int StartNumber { get; set; }
        public int CurrentNumber { get; set; }           // last bill number issued from this series
        public string CustomerType { get; set; }         // "B2B" / "B2C"
        public string TransactionType { get; set; }       // "Local" / "Interstate" / "Export"
        public string SuffixType { get; set; }            // "None" / "FinancialYear" / "Custom"
        public string SuffixValue { get; set; }           // FY format string, or the custom text itself
        public int IsActive { get; set; }
        public int IsDefault { get; set; }                 // 1 = pre-selected on Sales Entry when creating a new bill

        // Bool-friendly wrapper so the grid's RadioButton can bind directly
        // without a value converter; IsDefault itself stays an int to match
        // the sqlite column (0/1) and Dapper's mapping.
        public bool IsDefaultChecked => IsDefault == 1;
    }

    // ─────────────────────────────────────────────────────────────────────
    // SALES BILL HEADER
    // ─────────────────────────────────────────────────────────────────────
    public class SalesSummary
    {
        public string Prefix { get; set; }               // PK part 1 — FK to sales_series_master
        public int BillNo { get; set; }                   // PK part 2 — the raw autoincrement number
        public string FormattedBillNo { get; set; }        // UNIQUE — Prefix + BillNo + suffix, e.g. "GA-619-2627"
        public DateTime BillDate { get; set; }
        public string BillType { get; set; }               // "Cash" / "Credit"
        public string CustomerCode { get; set; }           // nullable — walk-in cash sales have none
        public string CustomerName { get; set; }
        public string CustomerAddress { get; set; }
        public string CustomerPhone { get; set; }

        // GST
        public string CustomerGstNo { get; set; }
        public string State { get; set; }
        public string StateCode { get; set; }

        public double GrossAmount { get; set; }
        public double DiscountAmount { get; set; }        // total discount (line-level + bill-level combined)
        public double DiscountPercent { get; set; }       // whole-bill discount % — applied on top of line discounts
        public double TaxAmount { get; set; }
        public double CashDiscount { get; set; }           // extra cash-discount knock-off, same idea as Purchase Entry
        public double RoundOff { get; set; }
        public double NetTotal { get; set; }

        // Settlement — how the net total was actually paid/received.
        // ToCredit is always the remainder (NetTotal - ToCash - ToBank), so
        // it's derived on screen but still persisted for reporting.
        public double ToCashAmount { get; set; }
        public double ToBankAmount { get; set; }
        public int BankHeadCode { get; set; }               // FK into accounts.db's ac_head_master, 0 = none
        public string BankHeadName { get; set; }            // denormalized convenience for display/reports
        public double ToCreditAmount { get; set; }

        // Transportation
        public string TransporterName { get; set; }
        public string VehicleNo { get; set; }
        public string LrNo { get; set; }
        public string LrDate { get; set; }
        public string EwayBillNo { get; set; }
        public string PlaceOfDelivery { get; set; }
        public string DateOfDelivery { get; set; }          // stored as TEXT ("yyyy-MM-dd"), nullable

        // Soft delete — bill stays in the DB, ledger effect reversed, excluded
        // from every calculation, and shown read-only with a big "CANCELLED"
        // watermark when reopened.
        public int IsCancelled { get; set; }

        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // SALES BILL LINE ITEMS
    // ─────────────────────────────────────────────────────────────────────
    public class SalesItemDetail
    {
        public string Prefix { get; set; }                // PK part 1
        public int BillNo { get; set; }                    // PK part 2
        public int SlNo { get; set; }                       // PK part 3

        public string ItemCode { get; set; }
        public string ItemName { get; set; }                // convenience — not authoritative, item_master is
        public int BatchId { get; set; }                     // which batch this line actually sold against
        public string BatchNumber { get; set; }              // joined from item_batch_details, display-only
        public int TransId { get; set; }                     // the ledger row this line created — needed to
                                                             // safely reverse/relink on Update/Cancel, same
                                                             // pattern as PurchaseItemDetail

        // This batch's MRP at the time of sale, persisted per-line (see
        // SalesRepository — new mrp column on sales_item_details, added via
        // AddColumnIfMissing self-heal). Needed because a reopened bill has
        // to show the MRP it actually sold at; item_batch_details.mrp is
        // the batch's current MRP and isn't guaranteed to still match what
        // this specific historical line used.
        public double Mrp { get; set; }

        public double Qty { get; set; }
        public double Rate { get; set; }

        // Ex-tax per-unit price, backed out of the tax-inclusive Rate at
        // entry time (Rate * 100/(100+TaxPercent)) and persisted so a
        // reopened bill shows exactly what it was calculated at, even if
        // the formula or tax rate configuration changes later. 0 for rows
        // saved before this column existed — SalesEntryView falls back to
        // re-deriving it from Rate/TaxPercent for those.
        public double UnitPrice { get; set; }
        public double DiscountPercent { get; set; }
        public double DiscountAmount { get; set; }
        public double TaxableAmount { get; set; }
        public double TaxPercent { get; set; }
        public double TaxAmount { get; set; }
        public double TotalAmount { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ITEM LOOKUP FOR THE SALES ENTRY COMBO — one row PER BATCH, not
    // aggregated. Customers pick a specific batch off the shelf manually
    // (there can be several batches of the same item live at once, at
    // different rates), so the combo has to show batch-level stock/rates,
    // not a single summed number per item.
    //
    // Items that have never been purchased (no batch exists at all) still
    // need to be sellable when the 0/negative-stock checkbox is on — those
    // show up as a single synthetic row with BatchId = 0 and CurrentStock = 0
    // ("(New Batch)"). Picking that row creates a real batch on the fly at
    // line-commit time using item_master's default MRP/rates, same as
    // Opening Stock's Path B.
    // ─────────────────────────────────────────────────────────────────────
    public class SalesItemLookup : INotifyPropertyChanged
    {
        private double _currentStock;

        public int BatchId { get; set; }                    // 0 = synthetic "no batch yet" row
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public string BatchNumber { get; set; }
        public string ExpiryDate { get; set; }
        public double Mrp { get; set; }
        public double PurchaseRate { get; set; }
        public double SalesRate { get; set; }
        public double TaxRate { get; set; }

        // This batch's own stock, not item-wide. Settable (not just
        // set-on-load) and notifying, because SalesEntryView decrements it
        // in place as lines are added to the current bill (and restores it
        // when a line is deleted) so the combo/group header and the
        // available-stock label under Quantity stay accurate without a
        // round-trip to the database for an unsaved bill.
        public double CurrentStock
        {
            get => _currentStock;
            set
            {
                if (_currentStock == value) return;
                _currentStock = value;
                OnChanged(nameof(CurrentStock));
                OnChanged(nameof(IsLowOrNegativeStock));
            }
        }

        public bool IsNewBatch => BatchId == 0;

        // True when CurrentStock <= 0 (and not a brand-new batch, which is
        // always exactly 0 by definition) — the combo's ItemTemplate
        // colors/flags these rows, and the entry line shows a small warning
        // if one is picked.
        public bool IsLowOrNegativeStock => CurrentStock <= 0;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // AcHeadLookup (AcHeadCode, AcHeadName) is already defined elsewhere in
    // BillPro.Models — Purchase Entry's own bank-settlement combo uses the
    // same lookup shape. Sales reuses that existing class rather than
    // redeclaring it here (that's what was causing the CS0101 duplicate-type
    // error). SalesRepository.GetBankAcHeads() and SalesEntryView's
    // AllBankHeadsList/cmbBankHead all bind against that shared type as-is —
    // no changes needed on their end.

    // ─────────────────────────────────────────────────────────────────────
    // GRID ROW MODEL — Sales Entry UI (bound to SfDataGrid), mirrors
    // PurchaseItemDetail's UI-model conventions (decimal, INotifyPropertyChanged)
    // ─────────────────────────────────────────────────────────────────────
    public class SalesLine : INotifyPropertyChanged
    {
        private string _itemCode;
        private string _itemName;
        private string _hsnCode;
        private int _batchId;
        private string _batchNumber;
        private decimal _mrp;
        private decimal _rate;
        private decimal _unitPrice;
        private decimal _quantity;
        private decimal _discountRate;
        private decimal _discount;
        private decimal _taxRate;
        private decimal _taxAmount;
        private decimal _taxableAmount;
        private decimal _total;
        private bool _isLowOrNegativeStock;

        // Populated once the line is actually saved — the trans_id this
        // line's SALE ledger row got, needed to reverse/relink on
        // Update/Cancel. Not user-editable.
        public int TransId { get; set; }

        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(nameof(ItemCode)); } }
        public string ItemName { get => _itemName; set { _itemName = value; OnChanged(nameof(ItemName)); } }
        public string HsnCode { get => _hsnCode; set { _hsnCode = value; OnChanged(nameof(HsnCode)); } }

        // The specific batch the customer picked off the shelf — set from
        // SalesItemLookup.BatchId when the line is added. 0 means "brand new
        // batch, never purchased before" (Path B — resolved into a real
        // batch_id by the repository at Save/Update time).
        public int BatchId { get => _batchId; set { _batchId = value; OnChanged(nameof(BatchId)); } }
        public string BatchNumber { get => _batchNumber; set { _batchNumber = value; OnChanged(nameof(BatchNumber)); } }

        public decimal Mrp { get => _mrp; set { _mrp = value; OnChanged(nameof(Mrp)); } }
        public decimal Rate { get => _rate; set { _rate = value; OnChanged(nameof(Rate)); } }

        // Tax now lives INSIDE Rate (Rate is what the customer pays per
        // unit, tax-inclusive) rather than being added on top of it.
        // UnitPrice is the backed-out ex-tax price — Rate * 100/(100+TaxRate)
        // — and everything downstream (gross, discount, taxable, tax, total)
        // is calculated off THIS, not Rate. Deliberately kept unrounded
        // here: rounding it to 2dp before multiplying by Quantity is exactly
        // what introduces the discrepancies this field exists to avoid — the
        // grid's NumberDecimalDigits="2" formats it to 2dp for display only,
        // the stored value stays full precision.
        public decimal UnitPrice { get => _unitPrice; set { _unitPrice = value; OnChanged(nameof(UnitPrice)); } }
        public decimal Quantity { get => _quantity; set { _quantity = value; OnChanged(nameof(Quantity)); } }
        public decimal DiscountRate { get => _discountRate; set { _discountRate = value; OnChanged(nameof(DiscountRate)); } }
        public decimal Discount { get => _discount; set { _discount = value; OnChanged(nameof(Discount)); } }
        public decimal TaxRate { get => _taxRate; set { _taxRate = value; OnChanged(nameof(TaxRate)); } }
        public decimal TaxAmount { get => _taxAmount; set { _taxAmount = value; OnChanged(nameof(TaxAmount)); } }
        public decimal TaxableAmount { get => _taxableAmount; set { _taxableAmount = value; OnChanged(nameof(TaxableAmount)); } }
        public decimal Total { get => _total; set { _total = value; OnChanged(nameof(Total)); } }

        // Set when this line was added while stock was 0/negative (Path B) —
        // the grid can flag the row visually.
        public bool IsLowOrNegativeStock { get => _isLowOrNegativeStock; set { _isLowOrNegativeStock = value; OnChanged(nameof(IsLowOrNegativeStock)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}