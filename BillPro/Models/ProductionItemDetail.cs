using BillPro.Helpers;

namespace BillPro.Models
{
    /// <summary>
    /// One row in the Production Entry consumption grid.
    /// Rows can originate from the selected item's formula (IsFormulaLine = true,
    /// auto-recalculated whenever Produced Qty changes) or be added manually via
    /// the entry line (IsFormulaLine = false, left untouched by recalculation).
    /// Either way the grid is fully editable — a formula-derived row edited by
    /// hand keeps the user's values until Produced Qty changes again.
    /// </summary>
    public class ProductionItemDetail : ObservableObject
    {
        private int _sNo;
        public int SNo
        {
            get => _sNo;
            set => SetProperty(ref _sNo, value);
        }

        private string _code = "";
        public string Code
        {
            get => _code;
            set => SetProperty(ref _code, value);
        }

        private string _itemName = "";
        public string ItemName
        {
            get => _itemName;
            set => SetProperty(ref _itemName, value);
        }

        private double _quantity;
        public double Quantity
        {
            get => _quantity;
            set { if (SetProperty(ref _quantity, value)) RecalcAmount(); }
        }

        private double _wastage;
        public double Wastage
        {
            get => _wastage;
            set { if (SetProperty(ref _wastage, value)) RecalcAmount(); }
        }

        private double _rate;
        public double Rate
        {
            get => _rate;
            set { if (SetProperty(ref _rate, value)) RecalcAmount(); }
        }

        private double _amount;
        public double Amount
        {
            get => _amount;
            set => SetProperty(ref _amount, value);
        }

        /// <summary>Per-unit quantity from the formula (Quantity ÷ ProducedQty). 0 for manually added rows.</summary>
        public double PerUnitQuantity { get; set; }

        /// <summary>True if this row came from the item's formula (auto-recalculated on Produced Qty change).</summary>
        public bool IsFormulaLine { get; set; }

        /// <summary>item_batch_details.batch_id this row's consumption was deducted from.
        /// Set by ProductionRepository when saved — 0 until then.</summary>
        public int BatchId { get; set; }

        /// <summary>stock_transaction_details.trans_id for this row's qty_out ledger
        /// posting. Set by ProductionRepository when saved — needed so Update/Delete
        /// can reverse exactly this row's consumption, same batch_id/trans_id pattern
        /// used by purchase_item_details.</summary>
        public int? TransId { get; set; }

        private void RecalcAmount()
        {
            Amount = System.Math.Round((Quantity + Wastage) * Rate, 2);
        }
    }
}