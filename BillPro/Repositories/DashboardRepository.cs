using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;

namespace BillPro.Repositories
{
    public class DashboardSummary
    {
        public int TodayBillCount { get; set; }
        public decimal TodaySalesAmount { get; set; }
        public decimal TodayProfit { get; set; }
        public decimal TodayItemsSold { get; set; }
        public decimal TodayPurchaseAmount { get; set; }
        public decimal CashInHand { get; set; }
        public List<DayTotal> Last7Days { get; set; } = new();
        public List<TopItem> TopItemsToday { get; set; } = new();
        public List<LowStockItem> LowStockItems { get; set; } = new();
        public List<RecentBill> RecentBills { get; set; } = new();
    }

    public class DayTotal
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
    }

    public class TopItem
    {
        public string ItemName { get; set; }
        public decimal Qty { get; set; }
    }

    public class LowStockItem
    {
        public string ItemName { get; set; }
        public decimal CurrentQty { get; set; }
    }

    public class RecentBill
    {
        public string FormattedBillNo { get; set; }
        public string CustomerName { get; set; }
        public decimal NetTotal { get; set; }
        public DateTime BillDate { get; set; }
    }

    /// <summary>
    /// Reads dashboard figures out of inventory.db / accounts.db, built
    /// against the actual sales_summary / sales_item_details / item_batch_details
    /// / View_CurrentStock / purchase_summary_details schema (see
    /// SalesRepository.cs, PurchaseRepository.cs, StockMasterRepository.cs).
    /// Cash-in-hand deliberately delegates to TaxLedgerRepository.
    /// GetCashInHandBalance so it can never disagree with the Day Book /
    /// Trial Balance's own Cash figure.
    /// </summary>
    public class DashboardRepository
    {
        // No reorder_level column exists on item_master today — this is a
        // simple, adjustable stand-in threshold for "running low". Wire it
        // up to a real per-item reorder level later if/when that field gets
        // added to Item Registration.
        private const double LowStockThreshold = 5;

        // NOTE: every COALESCE(SUM(...), 0) below is wrapped in CAST(... AS REAL).
        // Without it, SQLite returns the literal "0" as an INTEGER whenever a
        // group has no matching rows (e.g. an item with no stock movement),
        // while rows that do have data come back as REAL. Dapper can't
        // reliably unbox that mixed INTEGER/REAL result into a C# `decimal`
        // property and throws "Error parsing column N (...= 0 - Int64)" the
        // moment it hits a zero row. Forcing REAL keeps the column type
        // consistent across every row so Dapper's decimal conversion never fails.

        public DashboardSummary GetSummary()
        {
            var summary = new DashboardSummary();
            string today = DateTime.Today.ToString("yyyy-MM-dd");

            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                // ── Today's bills + sales amount ────────────────────────────
                var todayRow = db.QueryFirstOrDefault<(int Bills, decimal Amount)>(@"
                    SELECT COUNT(*), CAST(COALESCE(SUM(net_total), 0) AS REAL)
                    FROM sales_summary
                    WHERE bill_date = @Today AND is_cancelled = 0",
                    new { Today = today });
                summary.TodayBillCount = todayRow.Bills;
                summary.TodaySalesAmount = todayRow.Amount;

                // ── Today's items sold + rough profit (sale taxable value
                // minus the batch's own purchase rate * qty — same cost
                // basis Current Stock report uses) ──────────────────────────
                var soldRow = db.QueryFirstOrDefault<(decimal Qty, decimal Taxable, decimal Cost)>(@"
                    SELECT
                        CAST(COALESCE(SUM(d.qty), 0) AS REAL),
                        CAST(COALESCE(SUM(d.taxable_amount), 0) AS REAL),
                        CAST(COALESCE(SUM(d.qty * b.purchase_rate), 0) AS REAL)
                    FROM sales_item_details d
                    JOIN sales_summary s ON s.prefix = d.prefix AND s.bill_no = d.bill_no
                    LEFT JOIN item_batch_details b ON b.batch_id = d.batch_id
                    WHERE s.bill_date = @Today AND s.is_cancelled = 0",
                    new { Today = today });
                summary.TodayItemsSold = soldRow.Qty;
                summary.TodayProfit = soldRow.Taxable - soldRow.Cost;

                // ── Today's purchases (no is_cancelled column — Purchase
                // hard-deletes rather than soft-cancels) ───────────────────
                summary.TodayPurchaseAmount = db.ExecuteScalar<decimal>(@"
                    SELECT CAST(COALESCE(SUM(net_total), 0) AS REAL) FROM purchase_summary_details
                    WHERE purchase_date = @Today",
                    new { Today = today });

                // ── Last 7 days sales trend ──────────────────────────────────
                var from = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd");
                var dayLookup = db.Query<(string D, decimal Amt)>(@"
                    SELECT bill_date, CAST(COALESCE(SUM(net_total), 0) AS REAL)
                    FROM sales_summary
                    WHERE bill_date >= @From AND is_cancelled = 0
                    GROUP BY bill_date",
                    new { From = from })
                    .ToDictionary(r => r.D, r => r.Amt);

                for (int i = 6; i >= 0; i--)
                {
                    var d = DateTime.Today.AddDays(-i);
                    dayLookup.TryGetValue(d.ToString("yyyy-MM-dd"), out decimal amt);
                    summary.Last7Days.Add(new DayTotal { Date = d, Amount = amt });
                }

                // ── Top 5 items sold today ───────────────────────────────────
                summary.TopItemsToday = db.Query<TopItem>(@"
                    SELECT d.item_name AS ItemName, CAST(SUM(d.qty) AS REAL) AS Qty
                    FROM sales_item_details d
                    JOIN sales_summary s ON s.prefix = d.prefix AND s.bill_no = d.bill_no
                    WHERE s.bill_date = @Today AND s.is_cancelled = 0
                    GROUP BY d.item_code, d.item_name
                    ORDER BY Qty DESC
                    LIMIT 5",
                    new { Today = today }).ToList();

                // ── Low stock — aggregate current_qty per item across all its
                // batches (View_CurrentStock is per-batch), excluding
                // Non-Stockable (category 3), same convention as
                // GetCurrentStockReport. ─────────────────────────────────────
                summary.LowStockItems = db.Query<LowStockItem>(@"
                    SELECT m.item_name AS ItemName, CAST(COALESCE(SUM(v.current_qty), 0) AS REAL) AS CurrentQty
                    FROM item_master m
                    LEFT JOIN View_CurrentStock v ON v.item_code = m.item_code
                    WHERE m.category <> 3
                    GROUP BY m.item_code, m.item_name
                    HAVING COALESCE(SUM(v.current_qty), 0) <= @Threshold
                    ORDER BY CurrentQty ASC
                    LIMIT 6",
                    new { Threshold = LowStockThreshold }).ToList();

                // ── Recent bills ──────────────────────────────────────────────
                summary.RecentBills = db.Query<RecentBill>(@"
                    SELECT formatted_bill_no AS FormattedBillNo, customer_name AS CustomerName,
                           CAST(net_total AS REAL) AS NetTotal, bill_date AS BillDate
                    FROM sales_summary
                    WHERE is_cancelled = 0
                    ORDER BY bill_date DESC, bill_no DESC
                    LIMIT 6").ToList();
            }

            // Cash in hand — same running-balance mechanism the Day Book /
            // Trial Balance use, so this card can never disagree with them.
            try
            {
                summary.CashInHand = (decimal)new TaxLedgerRepository().GetCashInHandBalance(DateTime.Today);
            }
            catch
            {
                summary.CashInHand = 0;
            }

            return summary;
        }
    }
}