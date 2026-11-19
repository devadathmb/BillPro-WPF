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
    // PURCHASE REPORTS — split out of PurchaseRepository.cs, mirroring
    // SalesRepository.reports.cs. All four report queries for Purchase in one
    // place:
    //   - GetReport             (flat, one row per bill — used by
    //                            PurchaseReportView)
    //   - GetItemWiseReport      (one row per purchased item line, joined back
    //                            to its bill header)
    //   - GetSettlementReport    (one row per bill, showing how it was paid)
    //   - GetTaxRateBreakup      (one row per (bill, tax rate) — backs the
    //                            dynamic "Purchase@X%" columns on
    //                            PurchaseReportView, same idea as
    //                            SalesRepository.GetTaxRateBreakup)
    //
    // PurchaseRepository.cs already declares `public partial class
    // PurchaseRepository`, so this file's methods join that same class with
    // no further wiring needed.
    // ═══════════════════════════════════════════════════════════════════════
    public partial class PurchaseRepository
    {
        /// <summary>
        /// Purchase Report — one row per bill, filtered by purchase date range
        /// and (optionally) a vendor name / GST No search. Mirrors
        /// StockMasterRepository.GetCurrentStockReport's filter-parameter
        /// pattern: pass null/empty for "no filter".
        /// </summary>
        public List<PurchaseSummary> GetReport(DateTime fromDate, DateTime toDate, string vendorSearch = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(vendorSearch) ? null : "%" + vendorSearch.Trim() + "%";

                return db.Query<PurchaseSummary>($@"
                    SELECT {SummarySelectColumns}
                    FROM   purchase_summary_details
                    WHERE  purchase_date BETWEEN @From AND @To
                      AND (@Pattern IS NULL OR vendor_name LIKE @Pattern OR gst_no LIKE @Pattern OR bill_no LIKE @Pattern)
                    ORDER  BY purchase_date, purchase_no",
                    new { From = from, To = to, Pattern = pattern }).ToList();
            }
        }

        /// <summary>
        /// Item-wise Purchase Report — one row per purchased item line,
        /// joined back to its bill header. Search matches item name/code,
        /// vendor name, or bill no.
        /// </summary>
        public List<PurchaseItemReportRow> GetItemWiseReport(DateTime fromDate, DateTime toDate, string search = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

                return db.Query<PurchaseItemReportRow>($@"
                    SELECT
                        s.purchase_no    AS PurchaseNo,
                        i.purchase_date  AS PurchaseDate,
                        s.bill_no        AS BillNo,
                        s.bill_date      AS BillDate,
                        s.vendor_name    AS VendorName,
                        i.item_code      AS ItemCode,
                        i.item_name      AS ItemName,
                        i.hsn_code       AS HsnCode,
                        i.mrp            AS Mrp,
                        i.rate           AS Rate,
                        i.quantity       AS Quantity,
                        i.discount       AS Discount,
                        i.tax_rate       AS TaxRate,
                        i.tax_amount     AS TaxAmount,
                        i.price          AS Price
                    FROM   purchase_item_details i
                    JOIN   purchase_summary_details s ON s.purchase_no = i.purchase_no
                    WHERE  i.purchase_date BETWEEN @From AND @To
                      AND (@Pattern IS NULL OR i.item_name LIKE @Pattern OR i.item_code LIKE @Pattern
                           OR s.vendor_name LIKE @Pattern OR s.bill_no LIKE @Pattern)
                    ORDER  BY i.purchase_date, s.bill_no, i.id",
                    new { From = from, To = to, Pattern = pattern }).ToList();
            }
        }

        /// <summary>
        /// Cash / Bank / Credit Settlement Report — one row per bill, showing
        /// how much of the bill's net total was paid in cash, paid via bank,
        /// or left owing to the vendor as credit.
        /// </summary>
        public List<PurchaseSettlementReportRow> GetSettlementReport(DateTime fromDate, DateTime toDate, string search = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

                return db.Query<PurchaseSettlementReportRow>($@"
                    SELECT
                        purchase_no      AS PurchaseNo,
                        purchase_date    AS PurchaseDate,
                        bill_no          AS BillNo,
                        vendor_name      AS VendorName,
                        net_total        AS NetTotal,
                        to_cash_amount   AS ToCash,
                        to_card_amount   AS ToBank,
                        bank_head_name   AS BankHeadName,
                        to_credit_amount AS ToCredit
                    FROM   purchase_summary_details
                    WHERE  purchase_date BETWEEN @From AND @To
                      AND (@Pattern IS NULL OR vendor_name LIKE @Pattern OR gst_no LIKE @Pattern OR bill_no LIKE @Pattern)
                    ORDER  BY purchase_date, purchase_no",
                    new { From = from, To = to, Pattern = pattern }).ToList();
            }
        }

        /// <summary>Per-bill taxable amount split by tax rate — backs the
        /// Purchase Report's dynamic "Purchase@X%" columns. One row per
        /// (bill, tax rate) combination actually present in
        /// purchase_item_details for the matching bills. Same date-range /
        /// vendor-search filters as GetReport. Zero-taxable-amount groupings
        /// are excluded at the SQL level (HAVING SUM != 0).</summary>
        public List<PurchaseTaxRateBreakupRow> GetTaxRateBreakup(DateTime fromDate, DateTime toDate, string vendorSearch = null)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string from = fromDate.ToString("yyyy-MM-dd");
                string to = toDate.ToString("yyyy-MM-dd");
                string pattern = string.IsNullOrWhiteSpace(vendorSearch) ? null : "%" + vendorSearch.Trim() + "%";

                return db.Query<PurchaseTaxRateBreakupRow>(@"
                    SELECT
                        s.purchase_no        AS PurchaseNo,
                        i.tax_rate            AS TaxRate,
                        SUM(i.price)           AS TaxableAmount
                    FROM   purchase_item_details i
                    JOIN   purchase_summary_details s ON s.purchase_no = i.purchase_no
                    WHERE  s.purchase_date BETWEEN @From AND @To
                      AND (@Pattern IS NULL OR s.vendor_name LIKE @Pattern OR s.gst_no LIKE @Pattern
                           OR s.bill_no LIKE @Pattern)
                    GROUP  BY s.purchase_no, i.tax_rate
                    HAVING SUM(i.price) != 0",
                    new { From = from, To = to, Pattern = pattern }).ToList();
            }
        }
    }
}