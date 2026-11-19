namespace BillPro.Models
{
    /// <summary>Header row for a Production Entry — mirrors production_summary_details.</summary>
    public class ProductionSummary
    {
        public int ProductionNo { get; set; }
        public string ProductionDate { get; set; } = "";
        public string ItemCode { get; set; } = "";
        public string ItemName { get; set; } = "";   // display-only, joined from item_master
        public double ProducedQty { get; set; }
        public double ItemTotal { get; set; }
        public double LabourCharge { get; set; }
        public double OtherExpense { get; set; }
        public double GrandTotal { get; set; }
        public double AverageCost { get; set; }

        /// <summary>MRP the produced batch is being stocked at. Existing MRP => same batch
        /// (item_code+mrp) reused; new MRP => a brand-new batch is created (item_batch_details).</summary>
        public double Mrp { get; set; }

        /// <summary>item_batch_details.batch_id the produced stock landed in. 0 until saved.</summary>
        public int BatchId { get; set; }

        /// <summary>stock_transaction_details.trans_id for the PRODUCTION ledger row this
        /// entry created. Needed so Update/Delete can cleanly reverse it — same
        /// batch_id/trans_id pattern used by purchase_item_details.</summary>
        public int? TransId { get; set; }

        public string CreatedBy { get; set; } = "";
        public string CreatedDate { get; set; } = "";
        public string LastModifiedBy { get; set; } = "";
        public string LastModifiedDate { get; set; } = "";
    }

    /// <summary>
    /// One selectable row in the Production Entry "MRP / Batch" combo box —
    /// an existing batch (with its current stock) for the selected finished
    /// good. ToString() is overridden so that, when the editable ComboBox
    /// binds SelectedItem to its Text, only the plain MRP number shows up
    /// (the qty is shown separately via the dropdown's ItemTemplate).
    /// Typing an MRP that doesn't match any of these options is how a new
    /// batch gets created for that item.
    /// </summary>
    public class ProductionBatchOption
    {
        public double Mrp { get; set; }
        public double CurrentQty { get; set; }

        public override string ToString() => Mrp.ToString("0.00");
    }
}