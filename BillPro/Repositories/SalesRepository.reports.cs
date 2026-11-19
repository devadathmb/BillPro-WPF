using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // ═══════════════════════════════════════════════════════════════════════
    // SALES REPORTS — mirrors PurchaseRepository's GetReport / GetItemWiseReport
    // / GetSettlementReport, adapted for the sales_summary / sales_item_details
    // schema (composite Prefix+BillNo key, no vendor/GST-purchase columns).
    //
    // NOTE ON WIRING THIS IN:
    // SalesRepository.cs needs a one-word change — `public class SalesRepository`
    // becomes `public partial class SalesRepository` — so this file's methods
    // join that same class. Nothing else in SalesRepository.cs needs to change.
    //
    // Every method takes an optional seriesPrefixes filter: null or an empty
    // list means "all series" (no filter applied); a non-empty list restricts
    // results to bills whose Prefix is in that list — this is what backs the
    // new multi-select Series filter on all three report screens.
    // ═══════════════════════════════════════════════════════════════════════
    public partial class SalesRepository
    {
        /// <summary>Sales Report — one row per bill, filtered by bill date range,
        /// an optional free-text search (customer name/GST/bill no), and an
        /// optional set of series prefixes.</summary>
        public List<SalesSummary> GetReport(DateTime fromDate, DateTime toDate, string search = null,
            List<string> seriesPrefixes = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";
                bool hasSeriesFilter = seriesPrefixes != null && seriesPrefixes.Count > 0;

                return db.Query<SalesSummary>(SummarySelectColumns + @"
                    FROM   sales_summary
                    WHERE  bill_date BETWEEN @From AND @To
                      AND  is_cancelled = 0
                      AND (@Pattern IS NULL OR customer_name LIKE @Pattern OR customer_gst_no LIKE @Pattern
                           OR formatted_bill_no LIKE @Pattern)
                      AND (@HasSeriesFilter = 0 OR prefix IN @Prefixes)
                    ORDER  BY bill_date, prefix, bill_no",
                    new
                    {
                        From = from,
                        To = to,
                        Pattern = pattern,
                        HasSeriesFilter = hasSeriesFilter ? 1 : 0,
                        Prefixes = hasSeriesFilter ? seriesPrefixes : new List<string> { string.Empty }
                    }).ToList();
            }
        }

        /// <summary>Per-bill taxable amount split by GST rate — backs the
        /// Sales Report's dynamic "Sales@X%" columns. Returns one row per
        /// (bill, tax rate) combination actually present in
        /// sales_item_details for the matching bills; a bill that only ever
        /// sold at one rate contributes exactly one row. Same date-range /
        /// search / series filters as GetReport, applied via a join back to
        /// sales_summary so the two queries always agree on which bills are
        /// included.
        ///
        /// Zero-taxable-amount groupings are filtered out at the SQL level
        /// (HAVING SUM(...) != 0) — nothing here bothers the caller with a
        /// rate that nets to nothing, matching the "don't show columns with
        /// 0 values" requirement. (A single line's discount could still
        /// bring one line to 0, but the bill-level SUM per rate is what's
        /// filtered, not the individual line.)</summary>
        public List<SalesTaxRateBreakupRow> GetTaxRateBreakup(DateTime fromDate, DateTime toDate, string search = null,
            List<string> seriesPrefixes = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";
                bool hasSeriesFilter = seriesPrefixes != null && seriesPrefixes.Count > 0;

                return db.Query<SalesTaxRateBreakupRow>(@"
                    SELECT
                        s.prefix                    AS Prefix,
                        d.bill_no                   AS BillNo,
                        d.tax_percent                AS TaxPercent,
                        SUM(d.taxable_amount)         AS TaxableAmount
                    FROM   sales_item_details d
                    JOIN   sales_summary s ON s.prefix = d.prefix AND s.bill_no = d.bill_no
                    WHERE  s.bill_date BETWEEN @From AND @To
                      AND  s.is_cancelled = 0
                      AND (@Pattern IS NULL OR s.customer_name LIKE @Pattern OR s.customer_gst_no LIKE @Pattern
                           OR s.formatted_bill_no LIKE @Pattern)
                      AND (@HasSeriesFilter = 0 OR s.prefix IN @Prefixes)
                    GROUP  BY s.prefix, d.bill_no, d.tax_percent
                    HAVING SUM(d.taxable_amount) != 0",
                    new
                    {
                        From = from,
                        To = to,
                        Pattern = pattern,
                        HasSeriesFilter = hasSeriesFilter ? 1 : 0,
                        Prefixes = hasSeriesFilter ? seriesPrefixes : new List<string> { string.Empty }
                    }).ToList();
            }
        }

        /// <summary>Item-wise Sales Report — one row per sold item line, joined
        /// back to its bill header. Search matches item name/code, customer
        /// name, or bill no.</summary>
        public List<SalesItemReportRow> GetItemWiseReport(DateTime fromDate, DateTime toDate, string search = null,
            List<string> seriesPrefixes = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";
                bool hasSeriesFilter = seriesPrefixes != null && seriesPrefixes.Count > 0;

                return db.Query<SalesItemReportRow>($@"
                    SELECT
                        s.prefix              AS Prefix,
                        d.bill_no             AS BillNo,
                        s.formatted_bill_no   AS FormattedBillNo,
                        s.bill_date           AS BillDate,
                        s.customer_name       AS CustomerName,
                        d.item_code           AS ItemCode,
                        d.item_name           AS ItemName,
                        m.hsn_code            AS HsnCode,
                        b.batch_number        AS BatchNumber,
                        d.rate                AS Rate,
                        d.qty                 AS Quantity,
                        d.discount_amount     AS Discount,
                        d.tax_percent         AS TaxRate,
                        d.tax_amount          AS TaxAmount,
                        d.total_amount        AS Total
                    FROM   sales_item_details d
                    JOIN   sales_summary s        ON s.prefix = d.prefix AND s.bill_no = d.bill_no
                    LEFT JOIN item_master m        ON m.item_code = d.item_code
                    LEFT JOIN item_batch_details b ON b.batch_id = d.batch_id
                    WHERE  s.bill_date BETWEEN @From AND @To
                      AND  s.is_cancelled = 0
                      AND (@Pattern IS NULL OR d.item_name LIKE @Pattern OR d.item_code LIKE @Pattern
                           OR s.customer_name LIKE @Pattern OR s.formatted_bill_no LIKE @Pattern)
                      AND (@HasSeriesFilter = 0 OR s.prefix IN @Prefixes)
                    ORDER  BY s.bill_date, s.prefix, s.bill_no, d.sl_no",
                    new
                    {
                        From = from,
                        To = to,
                        Pattern = pattern,
                        HasSeriesFilter = hasSeriesFilter ? 1 : 0,
                        Prefixes = hasSeriesFilter ? seriesPrefixes : new List<string> { string.Empty }
                    }).ToList();
            }
        }

        /// <summary>Cash / Bank / Credit Settlement Report — one row per bill,
        /// showing how much of the bill's net total was received in cash,
        /// via bank, or left owing from the customer as credit.</summary>
        public List<SalesSettlementReportRow> GetSettlementReport(DateTime fromDate, DateTime toDate, string search = null,
            List<string> seriesPrefixes = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";
                bool hasSeriesFilter = seriesPrefixes != null && seriesPrefixes.Count > 0;

                return db.Query<SalesSettlementReportRow>($@"
                    SELECT
                        prefix              AS Prefix,
                        bill_no             AS BillNo,
                        formatted_bill_no   AS FormattedBillNo,
                        bill_date           AS BillDate,
                        customer_name       AS CustomerName,
                        net_total           AS NetTotal,
                        to_cash_amount      AS ToCash,
                        to_bank_amount      AS ToBank,
                        bank_head_name      AS BankHeadName,
                        to_credit_amount    AS ToCredit
                    FROM   sales_summary
                    WHERE  bill_date BETWEEN @From AND @To
                      AND  is_cancelled = 0
                      AND (@Pattern IS NULL OR customer_name LIKE @Pattern OR customer_gst_no LIKE @Pattern
                           OR formatted_bill_no LIKE @Pattern)
                      AND (@HasSeriesFilter = 0 OR prefix IN @Prefixes)
                    ORDER  BY bill_date, prefix, bill_no",
                    new
                    {
                        From = from,
                        To = to,
                        Pattern = pattern,
                        HasSeriesFilter = hasSeriesFilter ? 1 : 0,
                        Prefixes = hasSeriesFilter ? seriesPrefixes : new List<string> { string.Empty }
                    }).ToList();
            }
        }
    }
}