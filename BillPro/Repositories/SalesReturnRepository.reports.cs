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
    // SALES RETURN REPORTS — all three report queries for Sales Return in one
    // place:
    //   - GetReport            (flat, one row per return entry — used by
    //                            SalesReturnReportView)
    //   - GetItemWiseReport     (one row per return item line, joined back to
    //                            its entry header — mirrors SalesRepository's
    //                            GetItemWiseReport)
    //   - GetSettlementReport   (one row per return entry, showing how the
    //                            refund was actually settled — mirrors
    //                            SalesRepository's GetSettlementReport)
    //
    // All adapted for the sales_return_summary/sales_return_item_details
    // schema: single global entry_no, no series/prefix system.
    //
    // NOTE ON WIRING THIS IN:
    // SalesReturnRepository.cs needs a one-word change — `public class
    // SalesReturnRepository` becomes `public partial class
    // SalesReturnRepository` — so this file's methods join that same class.
    // ═══════════════════════════════════════════════════════════════════════
    public partial class SalesReturnRepository
    {
        private const string ReportSelectColumns = @"
            entry_no            AS EntryNo,
            entry_date          AS EntryDate,
            original_bill_no    AS OriginalBillNo,
            bill_type           AS BillType,
            customer_name       AS CustomerName,
            gross_amount        AS GrossAmount,
            discount_amount     AS DiscountAmount,
            tax_amount          AS TaxAmount,
            net_total           AS NetTotal,
            to_cash_amount      AS ToCashAmount,
            to_bank_amount      AS ToBankAmount,
            bank_head_name      AS BankHeadName,
            to_credit_amount    AS ToCreditAmount,
            is_cancelled        AS IsCancelled";

        /// <summary>Sales Return Report — one row per return entry, filtered by
        /// entry date range and an optional free-text search (customer name
        /// or original bill no). Cancelled entries are excluded, same
        /// convention as GetReport on the Sales side.</summary>
        public List<SalesReturnReportRow> GetReport(DateTime fromDate, DateTime toDate, string search = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

                var rows = db.Query<SalesReturnReportRow>($@"
                    SELECT {ReportSelectColumns}
                    FROM   sales_return_summary
                    WHERE  entry_date BETWEEN @From AND @To
                      AND  is_cancelled = 0
                      AND (@Pattern IS NULL OR customer_name LIKE @Pattern OR original_bill_no LIKE @Pattern)
                    ORDER  BY entry_date, entry_no",
                    new { From = from, To = to, Pattern = pattern }).ToList();

                foreach (var r in rows)
                    r.FormattedEntryNo = FormatEntryNo(r.EntryNo);

                return rows;
            }
        }

        /// <summary>Per-entry taxable amount split by tax rate — backs the
        /// Sales Return Report's dynamic "SalesReturn@X%" columns. One row
        /// per (entry, tax rate) combination actually present in
        /// sales_return_item_details for the matching entries. Same
        /// date-range / search filters as GetReport. Taxable amount is
        /// derived as total_amount - tax_amount (rather than assuming a
        /// dedicated taxable_amount column exists) so this works regardless
        /// of the exact schema. Zero-taxable-amount groupings are excluded
        /// at the SQL level (HAVING SUM != 0).</summary>
        public List<SalesReturnTaxRateBreakupRow> GetTaxRateBreakup(DateTime fromDate, DateTime toDate, string search = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

                return db.Query<SalesReturnTaxRateBreakupRow>(@"
                    SELECT
                        s.entry_no                          AS EntryNo,
                        i.tax_percent                        AS TaxPercent,
                        SUM(i.total_amount - i.tax_amount)    AS TaxableAmount
                    FROM   sales_return_item_details i
                    JOIN   sales_return_summary s ON s.entry_no = i.entry_no
                    WHERE  s.entry_date BETWEEN @From AND @To
                      AND  s.is_cancelled = 0
                      AND (@Pattern IS NULL OR s.customer_name LIKE @Pattern OR s.original_bill_no LIKE @Pattern)
                    GROUP  BY s.entry_no, i.tax_percent
                    HAVING SUM(i.total_amount - i.tax_amount) != 0",
                    new { From = from, To = to, Pattern = pattern }).ToList();
            }
        }

        /// <summary>Item-wise Sales Return Report — one row per return item
        /// line, filtered by entry date range and an optional free-text
        /// search (item name/code, customer name, or entry no). Cancelled
        /// entries are excluded.</summary>
        public List<SalesReturnItemReportRow> GetItemWiseReport(DateTime fromDate, DateTime toDate, string search = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

                var rows = db.Query<SalesReturnItemReportRow>(@"
                    SELECT
                        s.entry_no          AS EntryNo,
                        s.entry_date        AS EntryDate,
                        s.customer_name     AS CustomerName,
                        i.item_code         AS ItemCode,
                        i.item_name         AS ItemName,
                        m.hsn_code          AS HsnCode,
                        i.batch_number      AS BatchNumber,
                        i.rate              AS Rate,
                        i.qty               AS Quantity,
                        i.discount_amount   AS Discount,
                        i.tax_percent       AS TaxRate,
                        i.tax_amount        AS TaxAmount,
                        i.total_amount      AS Total
                    FROM   sales_return_item_details i
                    JOIN   sales_return_summary s ON s.entry_no = i.entry_no
                    LEFT JOIN item_master m ON m.item_code = i.item_code
                    WHERE  s.entry_date BETWEEN @From AND @To
                      AND  s.is_cancelled = 0
                      AND (@Pattern IS NULL
                           OR i.item_name LIKE @Pattern
                           OR i.item_code LIKE @Pattern
                           OR s.customer_name LIKE @Pattern
                           OR s.entry_no LIKE @Pattern)
                    ORDER  BY s.entry_date, s.entry_no",
                    new { From = from, To = to, Pattern = pattern }).ToList();

                foreach (var r in rows)
                    r.FormattedEntryNo = FormatEntryNo(r.EntryNo);

                return rows;
            }
        }

        /// <summary>Sales Return Cash / Bank / Credit Settlement Report — one
        /// row per return entry, showing how the refund was actually settled.</summary>
        public List<SalesReturnSettlementReportRow> GetSettlementReport(DateTime fromDate, DateTime toDate, string search = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

                var rows = db.Query<SalesReturnSettlementReportRow>(@"
                    SELECT
                        entry_no            AS EntryNo,
                        entry_date          AS EntryDate,
                        customer_name       AS CustomerName,
                        net_total           AS NetTotal,
                        to_cash_amount      AS ToCash,
                        to_bank_amount      AS ToBank,
                        bank_head_name      AS BankHeadName,
                        to_credit_amount    AS ToCredit
                    FROM   sales_return_summary
                    WHERE  entry_date BETWEEN @From AND @To
                      AND  is_cancelled = 0
                      AND (@Pattern IS NULL OR customer_name LIKE @Pattern OR entry_no LIKE @Pattern)
                    ORDER  BY entry_date, entry_no",
                    new { From = from, To = to, Pattern = pattern }).ToList();

                foreach (var r in rows)
                    r.FormattedEntryNo = FormatEntryNo(r.EntryNo);

                return rows;
            }
        }
    }
}