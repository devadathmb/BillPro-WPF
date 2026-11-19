using System;
using BillPro.Helpers;

namespace BillPro.Models
{
    // Which of the 4 entry forms triggered a posting. Sales/PurchaseReturn
    // both use "tax_paid" (money coming back to us via reversal), while
    // Sales/PurchaseReturn use "tax_collected" — see FinancialSyncService
    // for the exact debit/credit routing per type.
    public enum TransactionType
    {
        Sales,
        Purchase,
        SalesReturn,
        PurchaseReturn,

        // Receipt/Payment Entry postings — unlike the four bill types above,
        // these never resolve a party ledger via vendor_customer_master's
        // two-hop lookup: ac_head_code is selected directly from
        // AccountHeadRepository.GetAllForLookup(), which is already
        // accounts.db-native, so the settlement legs go straight in with no
        // cross-db hop at all. See ReceiptRepository.Accounts.cs /
        // PaymentRepository.Accounts.cs.
        Receipt,
        Payment,

        // Free-form double-entry voucher — any number of Debit/Credit legs
        // across any a/c heads, no tax-percent concept at all. Posted via
        // SettlementLegs only (Lines always empty), so every tax-grouping/
        // Discount/RoundOff path in FinancialSyncService is a natural
        // no-op for this type — see JournalEntryRepository.Accounts.cs.
        Journal
    }

    // ─────────────────────────────────────────────────────────────────────
    // opening_cash_balance — a single-row table holding the one-time
    // starting balance every daybook running-total calculation is anchored
    // to, instead of implicitly starting from zero before the earliest
    // posting. Entered once via the Opening Balance screen; see
    // TaxLedgerRepository.GetOpeningBalance/SetOpeningBalance and
    // GetBalanceBefore for how it folds into the report. No date attached —
    // it's a flat baseline applied on top of the full posting history.
    // ─────────────────────────────────────────────────────────────────────
    public class OpeningCashBalance
    {
        public double Amount { get; set; }                      // can be negative (e.g. if cash-in-hand started as an overdraft/shortfall)
        public string UpdatedBy { get; set; }
        public string UpdatedDate { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lightweight lookup row for the "select an account head" picker on
    // the Ledger Report — every real ac_head_master row (Cash, Bank,
    // customers/vendors, tax heads, expense heads, etc.), regardless of
    // category. See TaxLedgerRepository.GetAcHeadPickerList.
    // ─────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────
    // One row of the Ledger Report (Individual Heads) — every posting that
    // hit a single, user-picked ac_head within a date range, plus a running
    // balance. Sourced 1:1 from daybook_summary rows for that ledger_id,
    // same underlying data as the Daybook Report but filtered to one
    // account instead of showing every account for one day. Built by
    // TaxLedgerRepository.GetLedgerEntries — never persisted, purely a
    // report-time projection.
    // ─────────────────────────────────────────────────────────────────────
    public class LedgerReportRow
    {
        public string EntryDate { get; set; }                    // "yyyy-MM-dd"
        public string TransactionType { get; set; }               // "Sales" / "Purchase" / ... / "OpeningBalance"
        public string Narration { get; set; }                     // voucher ref, or "billFrom - billTo" for a grouped Sales/SalesReturn row
        public double DebitAmount { get; set; }
        public double CreditAmount { get; set; }
        public double RunningBalance { get; set; }

        // True only for the synthetic "Opening Balance" row inserted at the
        // top of the report — the balance on this ledger as of the instant
        // before the selected From date. Not stored anywhere; used by the
        // view to style/highlight the row, same convention as
        // DaybookLedgerRow.IsOpeningBalance / IndividualStockReportView's
        // opening row.
        public bool IsOpeningBalance { get; set; }

        // Ordering key — daybook_summary.id, with -1 reserved for the
        // synthetic opening row so it always sorts first.
        public long SortKey { get; set; }

        public string TransactionTypeLabel => IsOpeningBalance ? "Opening Balance" : TransactionType;

        public DateTime EntryDateValue => DateTime.Parse(EntryDate);
        public string EntryDateLabel => EntryDateValue.ToString("dd MMM, yyyy");
    }

    // ─────────────────────────────────────────────────────────────────────
    // One row of the Trial Balance — the closing balance of a single
    // ac_head as at a given date, split into whichever of Debit/Credit is
    // non-zero (never both). Sourced from TaxLedgerRepository
    // .GetTrialBalance: opening_debit - opening_credit (or
    // opening_cash_balance for the Cash head specifically), plus every
    // daybook_summary posting for that ledger with entry_date <= the
    // "as at" date. Heads that net to ~0 are dropped by the repository
    // before this row is ever built — a Trial Balance conventionally only
    // lists heads that still have something open, same idea as
    // View_CurrentStock hiding zero-stock items.
    // ─────────────────────────────────────────────────────────────────────
    public class TrialBalanceRow : IReportRow
    {
        public int LedgerId { get; set; }
        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }
        public double Debit { get; set; }
        public double Credit { get; set; }

        // True only for the synthetic "Total" row appended at the bottom —
        // built in the view (TrialBalanceReportView.LoadReport), not the
        // repository; there's only one total for the whole report rather
        // than one per group, so it doesn't need repository-side support
        // the way Daybook's per-day totals do.
        public bool IsTotalRow { get; set; }

        // ── IReportRow (print/PDF/Excel emphasis — see ReportColumn.cs) ────
        public bool IsReportHighlighted => IsTotalRow;
        public bool IsReportFieldBlank(string propertyName) => false;
        public int GetMergedLabelSpan(out string mergedLabel) { mergedLabel = null; return 0; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // One row of the Balance Sheet — a Liability-side item and an
    // Asset-side item printed side by side, same "two lists zipped into
    // one row" layout as the legacy screen (Liability | Asset, pipe-
    // separated). The two sides are independent lists of different
    // lengths (see TaxLedgerRepository.GetBalanceSheet), so a row may
    // carry only a Liability, only an Asset, or both — whichever side ran
    // out of items just leaves its Label/Amount null for that row.
    //
    // Only ac_head_master rows with ac_group_id = 0 (the "Balance Sheet"
    // group, same flag AccountHeadRepository.GetBalanceSheetHeads already
    // uses) are considered. Net Profit/Loss for the financial year to
    // date is folded in as a synthetic extra line — on the Liability side
    // if it's a profit, the Asset side if it's a loss — so the two totals
    // always balance, same as the "Profit" row in the reference printout.
    // ─────────────────────────────────────────────────────────────────────
    public class BalanceSheetRow : IReportRow
    {
        public string LiabilityParticulars { get; set; }
        public double? LiabilityAmount { get; set; }

        public string AssetParticulars { get; set; }
        public double? AssetAmount { get; set; }

        // True only for the synthetic "Total" row appended at the bottom.
        public bool IsTotalRow { get; set; }

        // Blank-safe display strings — whichever side ran out of items on
        // a given row has null Amount/Particulars, and a plain GridText
        // Column bound straight to a nullable double shows "0.00" rather
        // than a blank cell, so the grid binds to these instead.
        public string LiabilityAmountLabel => LiabilityAmount.HasValue ? LiabilityAmount.Value.ToString("N2") : "";
        public string AssetAmountLabel => AssetAmount.HasValue ? AssetAmount.Value.ToString("N2") : "";

        // ── IReportRow (print/PDF/Excel emphasis — see ReportColumn.cs) ────
        public bool IsReportHighlighted => IsTotalRow;
        public bool IsReportFieldBlank(string propertyName) => false;
        public int GetMergedLabelSpan(out string mergedLabel) { mergedLabel = null; return 0; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // One row of the Trading & Profit and Loss account — an Expense-side
    // item and an Income-side item printed side by side, same idea as
    // BalanceSheetRow but for the ac_head_master rows OUTSIDE the Balance
    // Sheet group (ac_group_id <> 0) — i.e. every head that isn't an
    // asset/liability. Amounts are period-bound (posted between the
    // selected From/To dates), not a running "as at" balance — a P&L is
    // always "for the period", never "as at a date".
    //
    // Net Profit (or Net Loss) for the period is folded in as a synthetic
    // extra line — "To Net Profit" on the Expense side if income exceeds
    // expense, "By Net Loss" on the Income side otherwise — so the two
    // totals always balance, same convention as the reference printout.
    // ─────────────────────────────────────────────────────────────────────
    public class ProfitLossRow : IReportRow
    {
        public string ExpenseParticulars { get; set; }
        public double? ExpenseAmount { get; set; }

        public string IncomeParticulars { get; set; }
        public double? IncomeAmount { get; set; }

        // True only for the synthetic "Total" row appended at the very
        // bottom of the report — the P&L section's grand total, with Net
        // Profit/Net Loss already folded in.
        public bool IsTotalRow { get; set; }

        // True only for the synthetic "Total" row appended at the bottom
        // of the Trading section (direct items, ac_group_id = 1) — the
        // Gross Profit/Gross Loss c/d figure is already folded in here,
        // same convention as IsTotalRow but one section up. Styled the
        // same as IsTotalRow in the view, just kept as a separate flag so
        // the view/report footer can still find the true final total via
        // IsTotalRow alone (LastOrDefault(r => r.IsTotalRow)).
        public bool IsGrossProfitRow { get; set; }

        // True only for the synthetic single-sided balancing lines —
        // Gross Profit/Loss c/d, Gross Profit/Loss b/d, Net Profit/Loss
        // for the period. Each of these gets appended as its own row
        // (see TaxLedgerRepository.AppendBalancingRow) with only one side
        // filled in, rather than being zipped in alongside real entries —
        // so it never ends up sharing a row with an unrelated real item
        // purely because they landed at the same list index. Styled
        // distinctly from ordinary rows and from the IsTotalRow/
        // IsGrossProfitRow "Total" rows so it's easy to spot at a glance.
        public bool IsBalancingRow { get; set; }

        // Blank-safe display strings — see BalanceSheetRow's identical
        // pair for why a plain nullable-double binding isn't used here.
        public string ExpenseAmountLabel => ExpenseAmount.HasValue ? ExpenseAmount.Value.ToString("N2") : "";
        public string IncomeAmountLabel => IncomeAmount.HasValue ? IncomeAmount.Value.ToString("N2") : "";

        // ── IReportRow (print/PDF/Excel emphasis — see ReportColumn.cs) ────
        public bool IsReportHighlighted => IsTotalRow || IsGrossProfitRow || IsBalancingRow;
        public bool IsReportFieldBlank(string propertyName) => false;
        public int GetMergedLabelSpan(out string mergedLabel) { mergedLabel = null; return 0; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Picker source for the Ledger Report's account-head dropdown — every
    // real account (income/expense heads, Cash, banks, customers, vendors,
    // tax heads, everything in ac_head_master). Deliberately unfiltered:
    // unlike the stock picker's "hide zero stock" idea, there's no
    // equivalent "hide heads with no transactions" default — a head with a
    // 0 balance may still be exactly what someone wants to check.
    // ─────────────────────────────────────────────────────────────────────
    public class AcHeadPickerLookup
    {
        public int LedgerId { get; set; }                        // ac_head_master.id
        public string AcHeadName { get; set; }
        public string AcHeadCode { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // One row of the Ledger Report (individual account head) — sourced
    // directly from daybook_summary filtered to a single ledger_id, ordered
    // chronologically, with RunningBalance computed in C# as each row is
    // read (see TaxLedgerRepository.GetLedgerHistory). Grouped rows
    // (VoucherRef null) already carry a bill-number-range narration from
    // BillNoFrom/BillNoTo, same as the Day Book report — see
    // TaxLedgerRepository.GetLedgerHistory for exactly how Narration is
    // built for those.
    // ─────────────────────────────────────────────────────────────────────
    public class LedgerTransactionReportRow : IReportRow, IReportRowEmphasis
    {
        public long SortKey { get; set; }                        // daybook_summary.id — chronological tie-breaker within a date
        public string EntryDate { get; set; }                     // "yyyy-MM-dd"
        public string TransactionType { get; set; }               // "Sales" / "Purchase" / "Receipt" / etc.
        public string Narration { get; set; }                     // VoucherRef, or "billFrom - billTo" for a grouped row
        public double DebitAmount { get; set; }
        public double CreditAmount { get; set; }
        public double RunningBalance { get; set; }

        // Raw grouping/identity fields behind Narration — only populated for
        // rows sourced from daybook_summary (not the synthetic Opening
        // Balance row). Series/BillNoFrom/BillNoTo are non-null only for a
        // grouped Sales/SalesReturn bucket (see FinancialSyncService
        // .UpsertGroupedDaybookRow); VoucherRef is non-null for every
        // granular row instead. Used by the view to drive the "jump to
        // entry" double-click — see EntryDrillDownHelper.
        public string VoucherRef { get; set; }
        public string Series { get; set; }
        public string BillNoFrom { get; set; }
        public string BillNoTo { get; set; }

        // True only for a grouped Sales/SalesReturn bucket — Narration is a
        // "billFrom - billTo" range rather than a single voucher, so a
        // double-click needs to open a bill picker instead of jumping
        // straight to one entry.
        public bool IsBillRangeRow => VoucherRef == null && !string.IsNullOrEmpty(Series);

        // True only for the synthetic "Opening Balance" row inserted at the
        // top of the filtered range — same idea as DaybookLedgerRow
        // .IsOpeningBalance / StockTransactionReportRow's opening row.
        public bool IsOpeningBalance { get; set; }

        // Classic ledger convention, same as DaybookLedgerRow.Prefix: a
        // credit entry reads "By ...", a debit entry reads "To ...".
        public string Prefix => CreditAmount != 0 ? "By" : (DebitAmount != 0 ? "To" : "");

        public double? Debit => DebitAmount != 0 ? (double?)DebitAmount : null;
        public double? Credit => CreditAmount != 0 ? (double?)CreditAmount : null;

        // Classic ledger convention for the running-balance / closing-balance
        // columns: never show a raw negative number — show the magnitude
        // suffixed with Dr (debit balance, i.e. RunningBalance < 0) or Cr
        // (credit balance, i.e. RunningBalance >= 0), since RunningBalance
        // is accumulated as Credit - Debit (see GetLedgerHistory). Shared via
        // the static FormatCrDr so the view's totals-bar labels (Closing
        // Balance / Current Balance) can format the same way without
        // duplicating the sign logic.
        public string RunningBalanceDisplay => FormatCrDr(RunningBalance);

        public static string FormatCrDr(double amount) =>
            $"{Math.Abs(amount):N2} {(amount < 0 ? "Dr" : "Cr")}";

        public DateTime EntryDateValue => DateTime.Parse(EntryDate);
        public string EntryDateLabel => EntryDateValue.ToString("dd MMM, yyyy");

        // ── IReportRow (print/PDF/Excel emphasis — see ReportColumn.cs) ────
        public bool IsReportHighlighted => false;
        public bool IsReportFieldBlank(string propertyName) => false;
        public int GetMergedLabelSpan(out string mergedLabel) { mergedLabel = null; return 0; }

        // Bold/slightly-bigger the first real transaction row of each new
        // date, so a flat printed ledger still reads as day-by-day blocks.
        // Null for the Opening Balance row so it's excluded from the
        // comparison entirely — it gets its own color-only callout instead
        // (see ReportRowBackground), no size change.
        public string ReportGroupKey => IsOpeningBalance ? null : EntryDate;

        // Soft amber tint for the Opening Balance row — color only, size
        // untouched (see ReportGroupKey above).
        public System.Windows.Media.Color? ReportRowBackground =>
            IsOpeningBalance ? System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7) : (System.Windows.Media.Color?)null;

        public double ReportExtraFontBump => 0;

        public bool ReportRowBottomBorder => false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // tax_ledger_mapping — one row per tax%, routing table into ac_head_master.
    // Rows are auto-created on first use of a new tax% (see TaxLedgerRepository
    // .GetOrCreateMapping) rather than hand-configured.
    // ─────────────────────────────────────────────────────────────────────
    public class TaxLedgerMapping
    {
        public double TaxPercent { get; set; }               // PK
        public int SalesLedgerId { get; set; }                 // ac_head_master.id — Sales @ X% (Income)
        public int PurchaseLedgerId { get; set; }               // Purchase @ X% (Expense)
        public int SalesReturnLedgerId { get; set; }             // Sales Return @ X% (contra-income)
        public int PurchaseReturnLedgerId { get; set; }          // Purchase Return @ X% (contra-expense)
        public int TaxCollectedLedgerId { get; set; }            // Tax Collected @ X% (Liability) — Sales, Purchase Return
        public int TaxPaidLedgerId { get; set; }                 // Tax Paid @ X% (Asset) — Purchase, Sales Return
    }

    // ─────────────────────────────────────────────────────────────────────
    // voucher_tax_postings — the audit trail / backup table. Still written
    // for EVERY leg of EVERY voucher, exactly as before, but no longer read
    // at report time. It is now load-bearing only for Update/Cancel deltas:
    // when a bill's summary contribution needs to be subtracted out of an
    // already-grouped daybook_summary row, this table is where the exact
    // prior debit/credit/series/role for that voucher is looked up.
    //
    // Series and SettlementType are the two additions for this architecture
    // change:
    //   - Series: the sales_series_master prefix this voucher's ref matched
    //     at posting time (Sales/SalesReturn only, null otherwise). Stored
    //     so Update/Cancel can find the exact grouped bucket a prior posting
    //     landed in without re-deriving it from the ref string again.
    //   - SettlementType: "Cash" / "Bank" / "Party", set only on
    //     LedgerRole = "Party" rows. Records which kind of settlement leg
    //     this was, since that decides grouped-vs-granular for Sales/
    //     SalesReturn and isn't otherwise recoverable from this table alone.
    // ─────────────────────────────────────────────────────────────────────
    public class VoucherTaxPosting
    {
        public int Id { get; set; }                            // PK, autoincrement
        public string TransactionType { get; set; }             // "Sales" / "Purchase" / "SalesReturn" / "PurchaseReturn" / "Receipt" / "Payment"
        public string VoucherRef { get; set; }                   // FormattedBillNo / FormattedEntryNo — traceability + Update/Cancel key
        public string EntryDate { get; set; }                    // "yyyy-MM-dd", drives which daybook day this rolls into
        public double TaxPercent { get; set; }
        public int LedgerId { get; set; }                        // ac_head_master.id this line hit
        public string LedgerRole { get; set; }                    // "Taxable" / "Tax" / "Discount" / "RoundOff" / "Party"
        public string Series { get; set; }                        // sales_series_master prefix, Sales/SalesReturn only — else null
        public string SettlementType { get; set; }                // "Cash" / "Bank" / "Party" — LedgerRole="Party" rows only, else null
        public double DebitAmount { get; set; }
        public double CreditAmount { get; set; }
        public int IsReversed { get; set; }                       // 1 = superseded by a later Update/Cancel reversal
        public string CreatedDate { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // daybook_summary — the LIVE table reports query against. Two
    // granularities live side by side, distinguished by whether VoucherRef
    // is null:
    //
    //   Grouped (VoucherRef IS NULL): one row per
    //   (TransactionType, EntryDate, Series, TaxPercent, LedgerRole, LedgerId)
    //   — only for Sales/SalesReturn's Taxable/Tax/Discount/RoundOff legs
    //   and Cash/Bank settlement legs. Many bills accumulate into one row.
    //
    //   Granular (VoucherRef IS NOT NULL): one row per
    //   (TransactionType, VoucherRef, LedgerId, LedgerRole, TaxPercent)
    //   — everything else: all of Purchase/PurchaseReturn/Receipt/Payment,
    //   and Sales/SalesReturn's own Party/credit settlement legs.
    //
    // Written directly by FinancialSyncService at Post/Reverse time
    // (incrementally accumulated for grouped rows), never derived via a
    // GROUP BY scan of voucher_tax_postings on the report's hot path.
    // ─────────────────────────────────────────────────────────────────────
    public class DaybookSummary
    {
        public int Id { get; set; }                              // PK, autoincrement
        public string TransactionType { get; set; }
        public string EntryDate { get; set; }
        public int LedgerId { get; set; }
        public string LedgerRole { get; set; }
        public double TaxPercent { get; set; }
        public string Series { get; set; }                       // null except grouped Sales/SalesReturn rows
        public string VoucherRef { get; set; }                    // null ONLY for a grouped row
        public string BillNoFrom { get; set; }                    // grouped rows only — first-seen FormattedBillNo, not shrunk on cancel
        public string BillNoTo { get; set; }                      // grouped rows only — most-recently-posted FormattedBillNo
        public double DebitAmount { get; set; }
        public double CreditAmount { get; set; }
        public string LastRefreshedDate { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // One row of the taxwise Daybook Report — everything posted for a given
    // tax% within the selected From/To date range, pivoted across the six
    // ledger roles from tax_ledger_mapping. Built by
    // TaxLedgerRepository.GetTaxwiseSummary — never persisted, it's a
    // report-time projection over daybook_summary.
    // ─────────────────────────────────────────────────────────────────────
    public class DaybookReportRow
    {
        public double TaxPercent { get; set; }
        public double SalesAmount { get; set; }
        public double PurchaseAmount { get; set; }
        public double SalesReturnAmount { get; set; }
        public double PurchaseReturnAmount { get; set; }
        public double TaxCollected { get; set; }
        public double TaxPaid { get; set; }

        // Net GST payable/receivable for this slab — Collected (from Sales/
        // PurchaseReturn) minus Paid (on Purchase/SalesReturn). Positive =
        // liability owed to the government for this slab.
        public double NetTax => TaxCollected - TaxPaid;

        public string TaxPercentLabel => TaxPercent % 1 == 0 ? $"{(int)TaxPercent}%" : $"{TaxPercent}%";
    }

    // ─────────────────────────────────────────────────────────────────────
    // One row of the date-wise Daybook Report — now sourced directly, 1:1,
    // from a row of daybook_summary (grouped or granular) joined to its
    // ledger name. Built by TaxLedgerRepository.GetDaybookEntries — never
    // persisted, it's a report-time projection, grouped by EntryDate in the
    // view to reproduce the classic "By .../To ..." Day Book printout.
    //
    // No more runtime aggregation happens here: a "Sales@11%" row for a day
    // is already exactly one row in daybook_summary, so this class is just
    // a display-shaping wrapper plus the two synthetic Balance b/f / Day
    // Total rows, same as before.
    // ─────────────────────────────────────────────────────────────────────
    public class DaybookLedgerRow : IReportRow, IReportRowEmphasis
    {
        public string EntryDate { get; set; }                    // "yyyy-MM-dd" — group key
        public string AcHeadName { get; set; }
        public string TransactionType { get; set; }
        public string VoucherRef { get; set; }                    // granular: the voucher ref; grouped: "billFrom - billTo"
        public double DebitAmount { get; set; }
        public double CreditAmount { get; set; }

        // Raw grouping/identity fields behind VoucherRef, used to drive the
        // "jump to entry" double-click (see EntryDrillDownHelper) — only
        // populated for real posting rows, not the synthetic Balance b/f /
        // Day Total / date-header rows. Series/BillNoFrom/BillNoTo are
        // non-null only for a grouped Sales/SalesReturn bucket (see
        // FinancialSyncService.UpsertGroupedDaybookRow); for every granular
        // row VoucherRef itself already carries the real voucher reference,
        // and these three stay null. NOT the display string above — that's
        // still the already-combined VoucherRef.
        public string GroupSeries { get; set; }
        public string GroupBillNoFrom { get; set; }
        public string GroupBillNoTo { get; set; }

        // True only for a grouped Sales/SalesReturn bucket — VoucherRef is a
        // "billFrom - billTo" range rather than a single voucher, so a
        // double-click needs to open a bill picker instead of jumping
        // straight to one entry.
        public bool IsBillRangeRow => !string.IsNullOrEmpty(GroupSeries);

        // True only for the synthetic "Balance b/f" row TaxLedgerRepository
        // .GetDaybookEntries inserts at the top of each day — the running
        // total of every live posting (across all ledgers) before that day.
        // Not stored anywhere; used by the view to style the row so it
        // stands out from ordinary postings.
        public bool IsOpeningBalance { get; set; }

        // True only for the single report-level Opening Balance row the
        // view inserts as the very first line, above the whole day-by-day
        // section (see DaybookReportView.BuildReportOpeningBalanceRow) —
        // distinct from the per-day "Balance b/f" rows above, which stay
        // labeled "By Balance b/f" as before. This one reads
        // "Opening Balance - <date>" instead (see AccountHeadDisplay).
        public bool IsReportOpeningBalance { get; set; }

        // Ordering key within a day — daybook_summary.id for both grouped
        // and granular rows (grouped rows sort by when their bucket was
        // first created, which is close enough to chronological for
        // display purposes). Not shown in the grid; purely an ordering aid.
        public long SortKey { get; set; }

        // True only for the synthetic "Day Total" row TaxLedgerRepository
        // .GetDaybookEntries appends at the bottom of each day — computed
        // directly in C# from that day's real posting rows (explicitly
        // excluding the Balance b/f carry-forward), rather than relying on
        // SfDataGrid's GroupSummaryRows, which doesn't scope correctly on
        // this Syncfusion version. Not stored anywhere; view-only, same as
        // IsOpeningBalance.
        public bool IsDayTotal { get; set; }
        public double DayTotalReceipt { get; set; }
        public double DayTotalPayment { get; set; }

        // True only for the synthetic date-boundary marker
        // TaxLedgerRepository.GetDaybookEntries inserts at the very top of
        // each day, above Balance b/f — a dedicated, bold "new date starts
        // here" row for Print/PDF/Excel (the on-screen grid doesn't need
        // it; SfDataGrid already draws its own group header per date, so
        // the view filters this row out of what it shows on screen).
        public bool IsDateHeader { get; set; }

        // Classic ledger convention: a credit leg is a receipt ("By ..."),
        // a debit leg is a payment ("To ..."). Each posting row only ever
        // carries one side (see FinancialSyncService.InsertPosting), so
        // this is never ambiguous.
        public string Prefix => CreditAmount != 0 ? "By" : "To";

        public string AccountHeadDisplay =>
            IsDayTotal ? "Day Total" :
            IsDateHeader ? "" :
            IsReportOpeningBalance ? $"Opening Balance - {EntryDateValue:dd MMM, yyyy}" :
            $"{Prefix} {AcHeadName}";

        // Narration substitute — we don't store free-text narration per
        // posting, so the voucher reference (bill/entry number, or the
        // bill-number range for a grouped Sales/SalesReturn row) fills that
        // column, same as "JOY CAN RTN 150*5" style narrations in a manual
        // day book, just numeric/traceable instead of free text.
        public string Narration => VoucherRef;

        // Day Total carries its own pre-summed figures in both columns at
        // once (unlike a normal posting row, which only ever has one side) —
        // so it needs its own branch here instead of reusing the
        // Credit/DebitAmount-based logic above.
        public double? Receipt => IsDayTotal ? DayTotalReceipt : (CreditAmount != 0 ? (double?)CreditAmount : null);
        public double? Payment => IsDayTotal ? DayTotalPayment : (DebitAmount != 0 ? (double?)DebitAmount : null);

        public DateTime EntryDateValue => DateTime.Parse(EntryDate);
        public string EntryDateLabel => EntryDateValue.ToString("dd MMM, yyyy dddd");

        // ── IReportRow (print/PDF/Excel emphasis — see ReportColumn.cs) ────

        // Both the Day Total row and the date-header row get the existing
        // bold/larger treatment.
        public bool IsReportHighlighted => IsDayTotal || IsDateHeader;

        // Day Total and the date-header row both render every column of
        // their own normally (Day Total: Date + "Day Total" + totals;
        // header: Date only, see GetMergedLabelSpan below for how the rest
        // of the header row's columns end up blank). Nothing else needs
        // blanking — ordinary/Balance b/f rows don't reach this check for
        // Date/Account Head at all, since those two columns are merged
        // away below.
        public bool IsReportFieldBlank(string propertyName) =>
            IsDateHeader && propertyName != nameof(EntryDateLabel);

        // Date-header row: merges Date+Account Head so "01 Jul, 2026
        // Wednesday" reads left-aligned across the full width those two
        // columns share, same as before but explicitly left- rather than
        // center-aligned (negative span = left-aligned merge; see the
        // helpers' merge-draw code). Ordinary/Balance b/f rows: same
        // left-aligned Date+Account-Head merge, showing AccountHeadDisplay
        // ("By ..."/"To ...") — since their Date cell is always blank
        // anyway, this lets the account head text use that freed space
        // instead of sitting indented under the Account Head header. Day
        // Total keeps both columns separate (Date, then "Day Total").
        public int GetMergedLabelSpan(out string mergedLabel)
        {
            if (IsDayTotal) { mergedLabel = null; return 0; }

            mergedLabel = IsDateHeader ? EntryDateLabel : AccountHeadDisplay;
            return -2;
        }

        // No group-start bolding on ordinary rows — that's the dedicated
        // IsDateHeader row above (via IsReportHighlighted), so transaction
        // rows never carry emphasis of their own.
        public string ReportGroupKey => null;

        // Opening Balance: soft amber tint, color only, no size change.
        // Day Total: a light blue tint layered on top of its existing
        // bold/larger text. Date header: a slightly deeper blue/gray tint
        // so the day boundary itself pops as its own distinct block.
        public System.Windows.Media.Color? ReportRowBackground =>
            IsOpeningBalance ? System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7) :
            IsDayTotal ? System.Windows.Media.Color.FromRgb(0xDB, 0xEA, 0xFE) :
            IsDateHeader ? System.Windows.Media.Color.FromRgb(0xC7, 0xD2, 0xFE) :
            (System.Windows.Media.Color?)null;

        // A couple of points larger than the standard highlight bump, so
        // the date boundary reads a bit more prominently than Day Total.
        public double ReportExtraFontBump => IsDateHeader ? 1.5 : 0;

        // Underline just the date-header row, like a section heading.
        public bool ReportRowBottomBorder => IsDateHeader;
    }

    // ─────────────────────────────────────────────────────────────────────
    // One row of the date-wise Cash Book Report — sourced 1:1 from a row
    // of cash_transactions (see TaxLedgerRepository.GetCashbookEntries),
    // same "By .../To ..." day-grouped shape as DaybookLedgerRow/
    // GetDaybookEntries, just against the Cash ledger's own movements
    // instead of every other ledger's.
    //
    // cash_transactions never records which ledger the cash was
    // exchanged with (see FinancialSyncService.InsertCashBookRow — only
    // the Cash leg itself is stored), so there's no AcHeadName to show
    // here the way Daybook has one. TransactionType (Sales/Purchase/
    // Receipt/Payment/...) fills that role instead — same field
    // GetLedgerEntries already surfaces for the Cash ledger's plain
    // statement view.
    // ─────────────────────────────────────────────────────────────────────
    public class CashbookLedgerRow : IReportRow, IReportRowEmphasis
    {
        public string EntryDate { get; set; }                    // "yyyy-MM-dd" — group key
        public string TransactionType { get; set; }
        public string VoucherRef { get; set; }
        public double DebitAmount { get; set; }
        public double CreditAmount { get; set; }

        // True only for the synthetic "Balance b/f" row TaxLedgerRepository
        // .GetCashbookEntries inserts at the top of each day — the running
        // cash-in-hand balance before that day (opening_cash_balance plus
        // every cash_transactions row before it — same figure
        // GetCashInHandBalance/GetLedgerBalanceBefore already compute).
        public bool IsOpeningBalance { get; set; }

        // True only for the single report-level Opening Balance row the
        // view inserts as the very first line, above the whole day-by-day
        // section (see CashbookReportView.BuildReportOpeningBalanceRow) —
        // distinct from the per-day "Balance b/f" rows above. This one
        // reads "Opening Balance - <date>" instead (see AccountHeadDisplay).
        public bool IsReportOpeningBalance { get; set; }

        // Ordering key within a day — cash_transactions.id.
        public long SortKey { get; set; }

        // True only for the synthetic "Day Total" row appended at the
        // bottom of each day — that day's own Receipt/Payment totals,
        // excluding the Balance b/f carry-forward, same convention as
        // DaybookLedgerRow.IsDayTotal.
        public bool IsDayTotal { get; set; }
        public double DayTotalReceipt { get; set; }
        public double DayTotalPayment { get; set; }

        // True only for the synthetic date-boundary marker row, same as
        // DaybookLedgerRow.IsDateHeader — used by Print/PDF/Excel; the
        // on-screen grid relies on RowStyle/AlternatingRowStyle triggers
        // instead (AllowGrouping="False", same as the Day Book).
        public bool IsDateHeader { get; set; }

        // Cash book convention — the OPPOSITE of the Day Book's for every
        // other ledger, because Cash is an asset: a debit leg increases
        // it (money coming in, a receipt, "By ..."), a credit leg
        // decreases it (money going out, a payment, "To ...") — same
        // Debit-increases/Credit-decreases rule GetCashInHandBalance's
        // own doc comment spells out. Each row only ever carries one
        // side (see FinancialSyncService.PostLeg), so this is never
        // ambiguous.
        public string Prefix => DebitAmount != 0 ? "By" : "To";

        public string AccountHeadDisplay =>
            IsDayTotal ? "Day Total" :
            IsDateHeader ? "" :
            IsReportOpeningBalance ? $"Opening Balance - {EntryDateValue:dd MMM, yyyy}" :
            $"{Prefix} {TransactionType}";

        // Narration substitute — the voucher/entry reference, same role
        // VoucherRef plays on the Day Book.
        public string Narration => VoucherRef;

        public double? Receipt => IsDayTotal ? DayTotalReceipt : (DebitAmount != 0 ? (double?)DebitAmount : null);
        public double? Payment => IsDayTotal ? DayTotalPayment : (CreditAmount != 0 ? (double?)CreditAmount : null);

        public DateTime EntryDateValue => DateTime.Parse(EntryDate);
        public string EntryDateLabel => EntryDateValue.ToString("dd MMM, yyyy dddd");

        // ── IReportRow (print/PDF/Excel emphasis — see ReportColumn.cs) ────
        public bool IsReportHighlighted => IsDayTotal || IsDateHeader;

        public bool IsReportFieldBlank(string propertyName) =>
            IsDateHeader && propertyName != nameof(EntryDateLabel);

        public int GetMergedLabelSpan(out string mergedLabel)
        {
            if (IsDayTotal) { mergedLabel = null; return 0; }

            mergedLabel = IsDateHeader ? EntryDateLabel : AccountHeadDisplay;
            return -2;
        }

        public string ReportGroupKey => null;

        public System.Windows.Media.Color? ReportRowBackground =>
            IsOpeningBalance ? System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7) :
            IsDayTotal ? System.Windows.Media.Color.FromRgb(0xDB, 0xEA, 0xFE) :
            IsDateHeader ? System.Windows.Media.Color.FromRgb(0xC7, 0xD2, 0xFE) :
            (System.Windows.Media.Color?)null;

        public double ReportExtraFontBump => IsDateHeader ? 1.5 : 0;

        public bool ReportRowBottomBorder => IsDateHeader;
    }
}