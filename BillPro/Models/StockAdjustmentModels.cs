using System;
using System.Collections.Generic;
using System.Linq;
using BillPro.Helpers;

namespace BillPro.Models
{
    /// <summary>
    /// Header row for an Excess Stock Entry or a Short Stock Entry. Excess
    /// and Short are two fully separate modules (ExcessStockRepository /
    /// ShortStockRepository, each with their own table pair and their own
    /// entry view) that happen to share the same shape today — this class
    /// is that shared shape, not a sign the two are secretly one thing.
    /// Expect it to fork into two distinct classes the day one side needs a
    /// field the other doesn't.
    ///
    /// These entries only move stock (item_batch_details / stock_transaction_details)
    /// for damages, breakage, physical-count corrections, etc. — they never
    /// touch accounts, and (unlike Purchase Entry / Production Entry) never
    /// write back to item_master.unit_price_purchase or item_batch_details.purchase_rate.
    /// PurchaseRate on each line is captured purely for this entry's own
    /// record/valuation — a snapshot, not a rate update.
    /// </summary>
    public class StockAdjustmentSummary
    {
        public int EntryNo { get; set; }

        /// <summary>"EX-<EntryNo>" for Excess, "SH-<EntryNo>" for Short — set
        /// by ExcessStockRepository.FormatEntryNo / ShortStockRepository.FormatEntryNo
        /// wherever a summary is loaded/returned. Excess and Short each have
        /// their own independent numbering, so the prefix is what actually
        /// disambiguates "Entry 1" excess from "Entry 1" short in
        /// reports/search/print.</summary>
        public string FormattedEntryNo { get; set; } = "";

        public string EntryDate { get; set; } = "";
        public string Remarks { get; set; } = "";

        // Collapsed totals for display — sum of each line's PurchaseRate and
        // Qty, recomputed from the lines whenever the entry is loaded/saved.
        public double TotalPurchaseRate { get; set; }
        public double TotalQty { get; set; }

        public string CreatedBy { get; set; } = "";
        public string CreatedDate { get; set; } = "";
        public string LastModifiedBy { get; set; } = "";
        public string LastModifiedDate { get; set; } = "";

        public int IsCancelled { get; set; }
        public string CancelledBy { get; set; }
        public string CancelledDate { get; set; }
    }

    /// <summary>
    /// One row in the Excess Stock Entry or Short Stock Entry grid. Reusing
    /// the same editable-row conventions as ProductionItemDetail: fully
    /// editable after being added, batch/ledger linkage populated only once
    /// saved. Short Stock Entry keeps Mrp read-only in its UI (no
    /// AllowEditing on the grid column, no editable field on the entry
    /// line) — it's still a plain settable property here because the value
    /// still needs to flow in from whichever batch was picked.
    /// </summary>
    public class StockAdjustmentLine : ObservableObject
    {
        private int _sNo;
        public int SNo
        {
            get => _sNo;
            set => SetProperty(ref _sNo, value);
        }

        private string _itemCode = "";
        public string ItemCode
        {
            get => _itemCode;
            set => SetProperty(ref _itemCode, value);
        }

        private string _itemName = "";
        public string ItemName
        {
            get => _itemName;
            set => SetProperty(ref _itemName, value);
        }

        private double _mrp;
        public double Mrp
        {
            get => _mrp;
            set => SetProperty(ref _mrp, value);
        }

        private double _purchaseRate;
        public double PurchaseRate
        {
            get => _purchaseRate;
            set => SetProperty(ref _purchaseRate, value);
        }

        private double _qty;
        public double Qty
        {
            get => _qty;
            set => SetProperty(ref _qty, value);
        }

        /// <summary>item_batch_details.batch_id this line's adjustment posted
        /// against. Set by the repository when saved — 0 until then.</summary>
        public int BatchId { get; set; }

        /// <summary>stock_transaction_details.trans_id for this line's ledger
        /// posting — needed so Update/Delete can reverse exactly this row,
        /// same pattern used by production_item_details/purchase_item_details.</summary>
        public int? TransId { get; set; }

        /// <summary>0 for new/unsaved lines — set once this row has an actual
        /// detail row in the DB, so Update knows it already exists.</summary>
        public int DetailId { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ITEM-WISE REPORT ROW — shared shape backing BOTH
    // ExcessStockItemReportView and ShortStockItemReportView (each report
    // queries only its own repository/table, so a given report's rows are
    // always all-Excess or all-Short — there's no discriminator field here
    // because there's no mixing to discriminate).
    // ═══════════════════════════════════════════════════════════════════════
    public class StockAdjustmentItemReportRow
    {
        public int EntryNo { get; set; }
        public string FormattedEntryNo { get; set; }
        public DateTime EntryDate { get; set; }
        public string Remarks { get; set; }

        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }
        public string BatchNumber { get; set; }

        public double Mrp { get; set; }
        public double PurchaseRate { get; set; }
        public double Qty { get; set; }

        // Used only by the export/print "— TOTAL —" rolled-up row — same
        // convention as SalesItemReportRow.IsSummaryRow.
        public bool IsSummaryRow { get; set; }
    }

    // One row per item — the outer Master-Details grid row. Carries its own
    // Details collection of StockAdjustmentItemReportRow for the nested
    // grid, same shape as SalesReturnItemSummaryRow.
    public class StockAdjustmentItemSummaryRow
    {
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string HsnCode { get; set; }

        public double TotalQty { get; set; }
        public double TotalPurchaseRate { get; set; }

        public List<StockAdjustmentItemReportRow> Details { get; set; } = new();

        public static List<StockAdjustmentItemSummaryRow> BuildFrom(IEnumerable<StockAdjustmentItemReportRow> rows)
        {
            return rows
                .GroupBy(r => r.ItemCode)
                .Select(g => new StockAdjustmentItemSummaryRow
                {
                    ItemCode = g.Key,
                    ItemName = g.First().ItemName,
                    HsnCode = g.First().HsnCode,
                    TotalQty = g.Sum(r => r.Qty),
                    TotalPurchaseRate = g.Sum(r => r.PurchaseRate),
                    Details = g.OrderByDescending(r => r.EntryDate).ToList()
                })
                .OrderBy(s => s.ItemName)
                .ToList();
        }
    }
} 