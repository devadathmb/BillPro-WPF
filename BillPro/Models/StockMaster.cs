using System;
using System.ComponentModel;

namespace BillPro.Models
{
    public class ItemBatchDetail
    {
        public int BatchId { get; set; }
        public string ItemCode { get; set; }
        public double Mrp { get; set; }
        public double PurchaseRate { get; set; }
        public double SalesRate { get; set; }
        public string BatchNumber { get; set; }
        public string ExpiryDate { get; set; }
    }

    public class StockTransactionDetail
    {
        public int TransId { get; set; }
        public string TransDate { get; set; }
        public string TransType { get; set; }   // OPENING, PURCHASE, SALE, PUR_RET
        public int BatchId { get; set; }
        public double QtyIn { get; set; }
        public double QtyOut { get; set; }
        public int? GodownId { get; set; }
    }

    public class CurrentStockView
    {
        public int BatchId { get; set; }
        public string ItemCode { get; set; }
        public double Mrp { get; set; }
        public double PurchaseRate { get; set; }
        public double SalesRate { get; set; }
        public string BatchNumber { get; set; }
        public string ExpiryDate { get; set; }
        public double CurrentQty { get; set; }
    }

    // Row for the Current Stock report — one row per batch, stock computed
    // "as of" a given date (only ledger movements up to and including that
    // date count), with company/category joined in from item_master for
    // filtering/display.
    public class CurrentStockReportRow : BillPro.Helpers.IReportRow
    {
        public int BatchId { get; set; }
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string BatchNumber { get; set; }
        public string CompanyName { get; set; }
        public string CategoryName { get; set; }
        public double Mrp { get; set; }
        public double SalesRate { get; set; }
        public double PurchaseRate { get; set; }
        public double CurrentQty { get; set; }

        // Amount is normally CurrentQty * PurchaseRate — computed
        // automatically for every real per-batch row loaded from the
        // database, unchanged from before. The export/print-only "— TOTAL —"
        // / rolled-up summary rows CurrentStockReportView builds for its
        // expand-aware Print/PDF/Excel need to show a genuine SUM of each
        // batch's own Amount instead, which can't be reconstructed by
        // multiplying a summed CurrentQty by any single PurchaseRate (batches
        // can carry different rates). AmountOverride lets those synthetic
        // rows supply that sum directly; ordinary rows never set it, so they
        // keep exactly the original computed behavior.
        public double? AmountOverride { get; set; }
        public double Amount => AmountOverride ?? CurrentQty * PurchaseRate;

        // True only for the synthetic "— TOTAL —" / collapsed-rollup rows
        // built by CurrentStockReportView's export logic — never true for a
        // row loaded from GetCurrentStockReport. Drives bold rendering and
        // blanking of per-batch-only fields (MRP/rates) that don't have one
        // single meaningful value once multiple batches are combined.
        public bool IsSummaryRow { get; set; }

        public bool IsReportHighlighted => IsSummaryRow;

        public bool IsReportFieldBlank(string propertyName)
        {
            if (!IsSummaryRow) return false;
            return propertyName == nameof(Mrp)
                || propertyName == nameof(SalesRate)
                || propertyName == nameof(PurchaseRate);
        }

        public int GetMergedLabelSpan(out string mergedLabel)
        {
            mergedLabel = null;
            return 0;
        }
    }

    // Aggregated report row — one row per batch, with all OPENING ledger
    // movements for that batch (item_code+mrp) summed into TotalQty.
    // Reusable pattern: reports bind to a flat row model like this,
    // built by a single aggregating repository query.
    public class OpeningStockReportRow
    {
        public int BatchId { get; set; }
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public decimal Mrp { get; set; }
        public decimal SalesRate { get; set; }
        public decimal PurchaseRate { get; set; }
        public decimal TotalQty { get; set; }
        public decimal Amount { get; set; }       // TotalQty * PurchaseRate — current stock value
        public int EntryCount { get; set; }      // how many opening-stock lines were combined
        public DateTime FirstEntryDate { get; set; }
        public DateTime LastEntryDate { get; set; }
    }

    public class OpeningStockMaster
    {
        public string EntryNo { get; set; }
        public System.DateTime EntryDate { get; set; }
        public string Remarks { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }

    public class OpeningStockDetail
    {
        public int DetailId { get; set; }
        public string EntryNo { get; set; }
        public string ItemCode { get; set; }
        public int BatchId { get; set; }
        public int TransId { get; set; }
        public double Qty { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }

    // Grid row model for the Opening Stock Entry UI (bound to SfDataGrid)
    public class OpeningStockLine : INotifyPropertyChanged
    {
        private string _itemCode;
        private string _itemName;
        private decimal _mrp;
        private decimal _qty;
        private decimal _salesRate;
        private decimal _purchaseRate;

        // 0 for new / unsaved lines — used to know whether to re-link on update
        public int DetailId { get; set; }
        public int BatchId { get; set; }
        public int TransId { get; set; }

        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(nameof(ItemCode)); } }
        public string ItemName { get => _itemName; set { _itemName = value; OnChanged(nameof(ItemName)); } }
        public decimal Mrp { get => _mrp; set { _mrp = value; OnChanged(nameof(Mrp)); } }
        public decimal Qty { get => _qty; set { _qty = value; OnChanged(nameof(Qty)); } }
        public decimal SalesRate { get => _salesRate; set { _salesRate = value; OnChanged(nameof(SalesRate)); } }
        public decimal PurchaseRate { get => _purchaseRate; set { _purchaseRate = value; OnChanged(nameof(PurchaseRate)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}