using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // One line item's tax-relevant figures, grouped later by TaxPercent.
    public class FinancialVoucherLine
    {
        public double TaxPercent { get; set; }
        public double TaxableValue { get; set; }
        public double TaxAmount { get; set; }
    }

    // Which real account a settlement leg represents. Recorded on
    // voucher_tax_postings (as SettlementType) purely for reference/
    // reporting — it does NOT affect grouping. Every settlement leg
    // (Cash, Bank, or Party) is always granular, one row per voucher, for
    // every transaction type: Cash and Bank are real accounts someone
    // reconciles against a passbook, same as a customer/vendor's own
    // ledger, so none of them are ever summarized into a shared bucket —
    // only the GST-construct legs (Taxable/Tax/Discount/RoundOff) group,
    // and only for Sales/SalesReturn.
    //
    // Defaults to Party, so Purchase/PurchaseReturn/Receipt/Payment's
    // settlement builders (which never set this) keep their existing
    // fully-granular behavior untouched.
    public enum SettlementLegType
    {
        Party,
        Cash,
        Bank
    }

    // How the net total was actually settled — Cash head, Bank head,
    // and/or the party's own ledger (customer/vendor ac_head). Mirrors the
    // Cash/Bank/Credit split already captured on every entry form
    // (ToCashAmount/ToBankAmount/ToCreditAmount), just expressed as ledger
    // legs instead of the flat fields SalesSummary etc. use.
    public class SettlementLeg
    {
        public int LedgerId { get; set; }
        public double DebitAmount { get; set; }
        public double CreditAmount { get; set; }
        public SettlementLegType LegType { get; set; } = SettlementLegType.Party;
    }

    public class FinancialVoucher
    {
        public TransactionType Type { get; set; }
        public string VoucherRef { get; set; }         // FormattedBillNo / FormattedEntryNo
        public string EntryDate { get; set; }           // "yyyy-MM-dd"
        public List<FinancialVoucherLine> Lines { get; set; } = new List<FinancialVoucherLine>();
        public List<SettlementLeg> SettlementLegs { get; set; } = new List<SettlementLeg>();

        // Bill-level (not per-tax-slab) adjustments — only meaningful for
        // Sales/Purchase/SalesReturn/PurchaseReturn. Zero = not posted at
        // all (no empty zero-amount ledger rows). See
        // FinancialSyncService.Post for the debit/credit routing.
        public double DiscountAmount { get; set; }
        public double RoundOffAmount { get; set; }
    }

    /// <summary>
    /// Single entry point for turning a Sales/Purchase/SalesReturn/PurchaseReturn/
    /// Receipt/Payment voucher into ledger postings. Call Post on Save, call Post
    /// again on Update (it reverses the bill's prior postings first, so it's safe
    /// to call repeatedly), and call Reverse on Cancel.
    ///
    /// Architecture (see project notes): voucher_tax_postings is written for
    /// EVERY leg of EVERY voucher, exactly as before, but is now a pure audit
    /// trail — never read at report time. daybook_summary is the live table
    /// reports query against, and this class writes to it directly and
    /// incrementally at Post/Reverse time:
    ///   - Purchase/PurchaseReturn/Receipt/Payment (all legs), and every
    ///     settlement leg of every type including Sales/SalesReturn's own
    ///     Cash/Bank/Party legs: one row per posting leg per voucher
    ///     (VoucherRef set) — same granularity as before.
    ///   - Sales/SalesReturn's Taxable/Tax/Discount/RoundOff legs only: one
    ///     row per (EntryDate, Series, TaxPercent, LedgerRole, LedgerId),
    ///     accumulated across every bill that lands in that bucket
    ///     (VoucherRef null).
    ///
    /// RefreshDaybookSummary remains available as a maintenance/rebuild
    /// action (e.g. a "Rebuild Daybook" button, or recovering from a bulk
    /// data fix) — it fully reconstructs daybook_summary from
    /// voucher_tax_postings from scratch, since Series and SettlementType
    /// are preserved on every posting row for exactly this purpose.
    /// </summary>
    public class FinancialSyncService
    {
        private readonly TaxLedgerRepository _taxLedgerRepo = new TaxLedgerRepository();

        private const string RoleTaxable = "Taxable";
        private const string RoleTax = "Tax";
        private const string RoleDiscount = "Discount";
        private const string RoleRoundOff = "RoundOff";
        private const string RoleParty = "Party";

        /// <summary>
        /// Posts (or re-posts) a voucher. Atomic: everything happens on one
        /// connection/transaction against accounts.db. Any prior, non-reversed
        /// postings for the same TransactionType+VoucherRef are reversed first,
        /// and their contribution is unwound from daybook_summary, so calling
        /// this from Update just works without a separate code path.
        /// </summary>
        public void Post(FinancialVoucher voucher, string currentUser)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        PostOnTransaction(db, tx, voucher);
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Same posting logic as Post, but runs on a transaction the caller
        /// already owns instead of opening its own — so a repository whose
        /// own table already lives in accounts.db (Receipt/Payment Entry)
        /// can insert its row and post its ledger legs as one atomic unit:
        /// either both commit or neither does. Caller commits/rolls back;
        /// this method never does either itself.
        /// </summary>
        public void PostOnTransaction(IDbConnection db, IDbTransaction tx, FinancialVoucher voucher)
        {
            // Self-heal: cash_transactions is only ever touched by a Cash-
            // tagged SettlementLeg (currently just Sales/SalesReturn — see
            // BuildSettlementLegs), so on an install where this table
            // predates that feature, its very first INSERT throws
            // "no such table". That exception used to roll back the
            // ENTIRE voucher (not just the cash leg) and get silently
            // swallowed by PostToAccounts's catch, so a cash sale would
            // "save" fine but post nothing at all anywhere in accounts.
            EnsureCashTransactionsTable(db, tx);

            // Undo this voucher's own prior contribution (if any) before
            // writing its new one — makes Update a plain "call Post again".
            var priorPostings = GetLivePostings(db, tx, voucher.Type, voucher.VoucherRef);
            ReversePostings(db, tx, voucher.Type, voucher.VoucherRef);
            UndoGroupedContributions(db, tx, voucher.Type, priorPostings);
            DeleteGranularRows(db, tx, voucher.Type, voucher.VoucherRef);

            string series = ResolveSeries(voucher.Type, voucher.VoucherRef);

            var grouped = voucher.Lines
                .GroupBy(l => l.TaxPercent)
                .Select(g => new
                {
                    TaxPercent = g.Key,
                    Taxable = g.Sum(x => x.TaxableValue),
                    Tax = g.Sum(x => x.TaxAmount)
                });

            foreach (var g in grouped)
            {
                var map = _taxLedgerRepo.GetOrCreateMapping(db, tx, g.TaxPercent);
                var (taxableLedger, taxLedger, taxableIsDebit, taxIsDebit) = ResolveLedgers(voucher.Type, map);

                PostLeg(db, tx, voucher, series, g.TaxPercent, taxableLedger, RoleTaxable,
                    debit: taxableIsDebit ? g.Taxable : 0, credit: taxableIsDebit ? 0 : g.Taxable);

                PostLeg(db, tx, voucher, series, g.TaxPercent, taxLedger, RoleTax,
                    debit: taxIsDebit ? g.Tax : 0, credit: taxIsDebit ? 0 : g.Tax);
            }

            PostDiscountAndRoundOff(db, tx, voucher, series);

            foreach (var leg in voucher.SettlementLegs)
            {
                PostLeg(db, tx, voucher, series, 0, leg.LedgerId, RoleParty,
                    debit: leg.DebitAmount, credit: leg.CreditAmount, legType: leg.LegType);
            }
        }

        /// <summary>
        /// Reverse, on a transaction the caller already owns — same
        /// relationship to Reverse as PostOnTransaction has to Post.
        /// </summary>
        public void ReverseOnTransaction(IDbConnection db, IDbTransaction tx, TransactionType type, string voucherRef, string entryDate)
        {
            EnsureCashTransactionsTable(db, tx);
            var priorPostings = GetLivePostings(db, tx, type, voucherRef);
            ReversePostings(db, tx, type, voucherRef);
            UndoGroupedContributions(db, tx, type, priorPostings);
            DeleteGranularRows(db, tx, type, voucherRef);
        }

        /// <summary>
        /// Reverses (flags is_reversed=1) all live postings for a voucher
        /// without re-posting, and unwinds their contribution from
        /// daybook_summary — used on Cancel. Safe to call even if nothing
        /// was ever posted for this ref (e.g. bill created before this
        /// feature existed).
        /// </summary>
        public void Reverse(TransactionType type, string voucherRef, string entryDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        ReverseOnTransaction(db, tx, type, voucherRef, entryDate);
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Maintenance/rebuild: fully reconstructs daybook_summary from the
        /// live (is_reversed = 0) rows in voucher_tax_postings, exactly
        /// reproducing the grouped/granular split of the live incremental
        /// path (using the Series/SettlementType columns preserved on each
        /// posting row). Pass a specific date for a scoped rebuild; call
        /// with null to rebuild the whole table — use for a manual "Rebuild
        /// Daybook" maintenance action or to recover after a bulk data fix.
        /// Not part of the normal Post/Reverse hot path anymore.
        /// </summary>
        public void RefreshDaybookSummary(string entryDate = null)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    try
                    {
                        EnsureCashTransactionsTable(db, tx);
                        RebuildDaybookSummaryFromPostings(db, tx, entryDate);
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// One-time migration entry point, meant to be wired to a button on
        /// the ReSync Settings screen (see ReSyncSettingsView) rather than
        /// run automatically — dropping and rebuilding daybook_summary is a
        /// deliberate, visible action, not something that should happen
        /// silently on a random app launch.
        ///
        /// Three steps, always in this order:
        ///   1. TaxLedgerRepository.MigrateLegacyDaybookSummaryIfNeeded() —
        ///      detects and drops an old-shape daybook_summary, if present.
        ///   2. TaxLedgerRepository.InitializeDatabase() — recreates the
        ///      table fresh with the current schema + both partial unique
        ///      indexes (harmless / no-op if step 1 found nothing to do,
        ///      since the table + indexes already exist in the current shape).
        ///   3. RefreshDaybookSummary(null) — full rebuild from
        ///      voucher_tax_postings, so the table isn't left empty after
        ///      a migration. Also safe (just a redundant full rebuild) to
        ///      run even when step 1 found nothing stale — e.g. as a
        ///      general "Rebuild Daybook" maintenance action.
        ///
        /// Returns true only when an actual legacy-table migration
        /// happened, so the caller can show a different message ("migrated
        /// and rebuilt" vs. "rebuilt — nothing needed migrating").
        /// </summary>
        public bool MigrateAndRebuildDaybookSummary()
        {
            bool migrated = _taxLedgerRepo.MigrateLegacyDaybookSummaryIfNeeded();
            _taxLedgerRepo.InitializeDatabase();
            RefreshDaybookSummary();
            return migrated;
        }

        // ── internals ────────────────────────────────────────────────────

        // Self-heal, same convention as AccountsSyncStatus.EnsureColumn
        // elsewhere in this codebase — CREATE TABLE IF NOT EXISTS is cheap
        // and safe to call on every Post/RefreshDaybookSummary, so nothing
        // has to depend on TaxLedgerRepository.InitializeDatabase() having
        // been updated to know about this table. Columns match exactly
        // what InsertCashBookRow writes and RebuildDaybookSummaryFromPostings
        // selects.
        private void EnsureCashTransactionsTable(IDbConnection db, IDbTransaction tx)
        {
            db.Execute(@"
                CREATE TABLE IF NOT EXISTS cash_transactions (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    transaction_type TEXT NOT NULL,
                    voucher_ref      TEXT,
                    entry_date       TEXT NOT NULL,
                    ledger_id        INTEGER NOT NULL,
                    debit_amount     REAL NOT NULL DEFAULT 0,
                    credit_amount    REAL NOT NULL DEFAULT 0,
                    created_date     TEXT
                );", transaction: tx);

            db.Execute(@"
                CREATE INDEX IF NOT EXISTS ix_cash_transactions_ref
                    ON cash_transactions (transaction_type, voucher_ref);", transaction: tx);

            db.Execute(@"
                CREATE INDEX IF NOT EXISTS ix_cash_transactions_date_ledger
                    ON cash_transactions (entry_date, ledger_id);", transaction: tx);
        }

        private void ReversePostings(IDbConnection db, IDbTransaction tx, TransactionType type, string voucherRef)
        {
            db.Execute(@"UPDATE voucher_tax_postings SET is_reversed = 1
                         WHERE transaction_type = @Type AND voucher_ref = @Ref AND is_reversed = 0",
                new { Type = type.ToString(), Ref = voucherRef }, tx);
        }

        private List<VoucherTaxPosting> GetLivePostings(IDbConnection db, IDbTransaction tx, TransactionType type, string voucherRef)
        {
            return db.Query<VoucherTaxPosting>(
                @"SELECT transaction_type AS TransactionType, voucher_ref AS VoucherRef, entry_date AS EntryDate,
                         tax_percent AS TaxPercent, ledger_id AS LedgerId, ledger_role AS LedgerRole,
                         series AS Series, settlement_type AS SettlementType,
                         debit_amount AS DebitAmount, credit_amount AS CreditAmount
                  FROM voucher_tax_postings
                  WHERE transaction_type = @Type AND voucher_ref = @Ref AND is_reversed = 0",
                new { Type = type.ToString(), Ref = voucherRef }, tx).AsList();
        }

        // Subtracts each prior posting's own contribution back out of
        // whichever grouped daybook_summary bucket it landed in. If a given
        // prior posting was actually granular (not grouped), the WHERE
        // voucher_ref IS NULL clause simply matches zero rows — harmless.
        // The granular rows themselves are removed separately in
        // DeleteGranularRows, so there's no double-handling.
        private void UndoGroupedContributions(IDbConnection db, IDbTransaction tx, TransactionType type, List<VoucherTaxPosting> priorPostings)
        {
            foreach (var p in priorPostings)
            {
                db.Execute(@"UPDATE daybook_summary
                             SET debit_amount = debit_amount - @Debit,
                                 credit_amount = credit_amount - @Credit,
                                 last_refreshed_date = datetime('now')
                             WHERE transaction_type = @Type AND entry_date = @Date AND ledger_role = @Role
                               AND tax_percent = @TaxPercent AND ledger_id = @LedgerId AND voucher_ref IS NULL
                               AND ((series IS NULL AND @Series IS NULL) OR series = @Series)",
                    new
                    {
                        Type = type.ToString(),
                        Date = p.EntryDate,
                        Role = p.LedgerRole,
                        TaxPercent = p.TaxPercent,
                        LedgerId = p.LedgerId,
                        Series = p.Series,
                        Debit = p.DebitAmount,
                        Credit = p.CreditAmount
                    }, tx);
            }
        }

        private void DeleteGranularRows(IDbConnection db, IDbTransaction tx, TransactionType type, string voucherRef)
        {
            db.Execute(@"DELETE FROM daybook_summary WHERE transaction_type = @Type AND voucher_ref = @Ref",
                new { Type = type.ToString(), Ref = voucherRef }, tx);

            // Cash legs live here instead of daybook_summary — see PostLeg/
            // InsertCashBookRow — so a re-post (Update) or Cancel needs to
            // clear this table too, same as the line above.
            db.Execute(@"DELETE FROM cash_transactions WHERE transaction_type = @Type AND voucher_ref = @Ref",
                new { Type = type.ToString(), Ref = voucherRef }, tx);
        }

        // Writes one leg to both voucher_tax_postings (audit trail, always)
        // and daybook_summary (grouped or granular, decided here).
        private void PostLeg(IDbConnection db, IDbTransaction tx, FinancialVoucher voucher, string series,
            double taxPercent, int ledgerId, string role, double debit, double credit,
            SettlementLegType legType = SettlementLegType.Party)
        {
            string settlementType = role == RoleParty ? legType.ToString() : null;

            InsertPosting(db, tx, voucher, series, taxPercent, ledgerId, role, debit, credit, settlementType);

            // Cash legs never land in daybook_summary at all — they go to
            // their own cash_transactions table instead, so the Day Book
            // (a general-ledger view across every OTHER account head) never
            // shows "By Cash"/"To Cash" rows, and cash-in-hand can be read
            // back with a single un-complicated SUM over a table that only
            // ever contains real cash movement. voucher_tax_postings above
            // still gets the full audit-trail row as usual.
            if (legType == SettlementLegType.Cash)
            {
                InsertCashBookRow(db, tx, voucher, ledgerId, debit, credit);
                return;
            }

            if (IsGroupedRole(voucher.Type, role, legType))
                UpsertGroupedDaybookRow(db, tx, voucher, series, taxPercent, ledgerId, role, debit, credit);
            else
                InsertGranularDaybookRow(db, tx, voucher, taxPercent, ledgerId, role, debit, credit);
        }

        // One row per voucher (never grouped, same as any other settlement
        // leg) in the dedicated cash table. Deleted and re-inserted on
        // Update exactly like a granular daybook_summary row would be — see
        // DeleteGranularRows, which now clears this table too.
        private void InsertCashBookRow(IDbConnection db, IDbTransaction tx, FinancialVoucher voucher, int ledgerId, double debit, double credit)
        {
            db.Execute(@"INSERT INTO cash_transactions (
                            transaction_type, voucher_ref, entry_date, ledger_id, debit_amount, credit_amount, created_date)
                         VALUES (@Type, @Ref, @Date, @LedgerId, @Debit, @Credit, datetime('now'))",
                new
                {
                    Type = voucher.Type.ToString(),
                    Ref = voucher.VoucherRef,
                    Date = voucher.EntryDate,
                    LedgerId = ledgerId,
                    Debit = debit,
                    Credit = credit
                }, tx);
        }

        // Only Sales/SalesReturn ever group anything, and only the
        // GST-construct legs: Taxable/Tax/Discount/RoundOff. Every
        // settlement leg — Cash, Bank, AND Party — always stays granular,
        // one row per voucher. Cash/Bank are real accounts someone
        // reconciles (e.g. a bank passbook), same reasoning as Party/
        // Customer, so they're never summarized — see project notes.
        // legType is accepted only so PostLeg's call site doesn't need a
        // separate signature; it no longer affects the result.
        private bool IsGroupedRole(TransactionType type, string role, SettlementLegType legType)
        {
            if (type != TransactionType.Sales && type != TransactionType.SalesReturn) return false;
            if (role == RoleParty) return false;

            return role == RoleTaxable || role == RoleTax || role == RoleDiscount || role == RoleRoundOff;
        }

        private void InsertPosting(IDbConnection db, IDbTransaction tx, FinancialVoucher voucher, string series,
            double taxPercent, int ledgerId, string role, double debit, double credit, string settlementType)
        {
            db.Execute(@"INSERT INTO voucher_tax_postings (
                            transaction_type, voucher_ref, entry_date, tax_percent, ledger_id,
                            ledger_role, series, settlement_type, debit_amount, credit_amount, is_reversed, created_date)
                         VALUES (
                            @Type, @Ref, @EntryDate, @TaxPercent, @LedgerId,
                            @Role, @Series, @SettlementType, @Debit, @Credit, 0, datetime('now'))",
                new
                {
                    Type = voucher.Type.ToString(),
                    Ref = voucher.VoucherRef,
                    voucher.EntryDate,
                    TaxPercent = taxPercent,
                    LedgerId = ledgerId,
                    Role = role,
                    Series = series,
                    SettlementType = settlementType,
                    Debit = debit,
                    Credit = credit
                }, tx);
        }

        // Accumulates into the shared (date, series, tax%, role, ledger)
        // bucket — creates it on first use. bill_no_from is set once and
        // never shrunk on cancel (approximate range, reference-only — see
        // project notes); bill_no_to is simply overwritten with whichever
        // voucher most recently touched this bucket.
        private void UpsertGroupedDaybookRow(IDbConnection db, IDbTransaction tx, FinancialVoucher voucher,
            string series, double taxPercent, int ledgerId, string role, double debit, double credit)
        {
            int updated = db.Execute(@"
                UPDATE daybook_summary
                SET debit_amount = debit_amount + @Debit,
                    credit_amount = credit_amount + @Credit,
                    bill_no_to = @Ref,
                    last_refreshed_date = datetime('now')
                WHERE transaction_type = @Type AND entry_date = @Date AND ledger_role = @Role
                  AND tax_percent = @TaxPercent AND ledger_id = @LedgerId AND voucher_ref IS NULL
                  AND ((series IS NULL AND @Series IS NULL) OR series = @Series)",
                new
                {
                    Type = voucher.Type.ToString(),
                    Date = voucher.EntryDate,
                    Role = role,
                    TaxPercent = taxPercent,
                    LedgerId = ledgerId,
                    Series = series,
                    Debit = debit,
                    Credit = credit,
                    Ref = voucher.VoucherRef
                }, tx);

            if (updated == 0)
            {
                db.Execute(@"INSERT INTO daybook_summary (
                                transaction_type, entry_date, ledger_id, ledger_role, tax_percent, series,
                                voucher_ref, bill_no_from, bill_no_to, debit_amount, credit_amount, last_refreshed_date)
                             VALUES (
                                @Type, @Date, @LedgerId, @Role, @TaxPercent, @Series,
                                NULL, @Ref, @Ref, @Debit, @Credit, datetime('now'))",
                    new
                    {
                        Type = voucher.Type.ToString(),
                        Date = voucher.EntryDate,
                        LedgerId = ledgerId,
                        Role = role,
                        TaxPercent = taxPercent,
                        Series = series,
                        Ref = voucher.VoucherRef,
                        Debit = debit,
                        Credit = credit
                    }, tx);
            }
        }

        // Prior granular rows for this voucher were already wiped in
        // DeleteGranularRows, so this is always a plain insert.
        private void InsertGranularDaybookRow(IDbConnection db, IDbTransaction tx, FinancialVoucher voucher,
            double taxPercent, int ledgerId, string role, double debit, double credit)
        {
            db.Execute(@"INSERT INTO daybook_summary (
                            transaction_type, entry_date, ledger_id, ledger_role, tax_percent, series,
                            voucher_ref, bill_no_from, bill_no_to, debit_amount, credit_amount, last_refreshed_date)
                         VALUES (
                            @Type, @Date, @LedgerId, @Role, @TaxPercent, NULL,
                            @Ref, NULL, NULL, @Debit, @Credit, datetime('now'))",
                new
                {
                    Type = voucher.Type.ToString(),
                    Date = voucher.EntryDate,
                    LedgerId = ledgerId,
                    Role = role,
                    TaxPercent = taxPercent,
                    Ref = voucher.VoucherRef,
                    Debit = debit,
                    Credit = credit
                }, tx);
        }

        // Sales/SalesReturn only — matches the voucher ref against the
        // longest registered sales_series_master.Prefix it starts with
        // (longest first, so e.g. "CB" isn't mistaken for "C"), same
        // matching rule the legacy report-time grouping used to use. Falls
        // back to the text before the first "-" for a ref that doesn't
        // match any currently-registered prefix (e.g. its series was since
        // renamed or deleted). Every other transaction type has no concept
        // of series, so this returns null for them.
        private string ResolveSeries(TransactionType type, string voucherRef)
        {
            if (type != TransactionType.Sales && type != TransactionType.SalesReturn) return null;
            if (string.IsNullOrEmpty(voucherRef)) return null;

            List<string> knownPrefixes;
            using (IDbConnection inventoryDb = DatabaseConfig.GetConnection())
                knownPrefixes = inventoryDb.Query<string>("SELECT prefix FROM sales_series_master")
                    .OrderByDescending(p => p.Length)
                    .ToList();

            var match = knownPrefixes.FirstOrDefault(p =>
                voucherRef.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            int dash = voucherRef.IndexOf('-');
            return dash > 0 ? voucherRef.Substring(0, dash) : voucherRef;
        }

        // Which ledger gets the taxable leg vs the tax leg, and whether each
        // is a debit or a credit, per transaction type. Matches the routing
        // described for the four voucher types:
        //   Sales            -> Credit Sales@X%,          Credit TaxCollected@X%
        //   Purchase         -> Debit  Purchase@X%,        Debit  TaxPaid@X%
        //   SalesReturn      -> Debit  SalesReturn@X%,     Debit  TaxPaid@X%      (reverses a prior sale)
        //   PurchaseReturn   -> Credit PurchaseReturn@X%,  Credit TaxCollected@X% (reverses a prior purchase)
        private (int taxableLedger, int taxLedger, bool taxableIsDebit, bool taxIsDebit) ResolveLedgers(
            TransactionType type, TaxLedgerMapping map)
        {
            switch (type)
            {
                case TransactionType.Sales:
                    return (map.SalesLedgerId, map.TaxCollectedLedgerId, false, false);
                case TransactionType.Purchase:
                    return (map.PurchaseLedgerId, map.TaxPaidLedgerId, true, true);
                case TransactionType.SalesReturn:
                    return (map.SalesReturnLedgerId, map.TaxPaidLedgerId, true, true);
                case TransactionType.PurchaseReturn:
                    return (map.PurchaseReturnLedgerId, map.TaxCollectedLedgerId, false, false);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        // Discount and Round Off are bill-level (tax_percent = 0 in the
        // posting row, same convention as the Party leg) rather than
        // per-slab. Only posted when non-zero, so a bill with no discount
        // and an exact round total leaves no rows behind at all.
        private void PostDiscountAndRoundOff(IDbConnection db, IDbTransaction tx, FinancialVoucher voucher, string series)
        {
            if (voucher.Type != TransactionType.Sales && voucher.Type != TransactionType.Purchase &&
                voucher.Type != TransactionType.SalesReturn && voucher.Type != TransactionType.PurchaseReturn)
                return;

            if (voucher.DiscountAmount == 0 && voucher.RoundOffAmount == 0)
                return;

            var (discountLedger, roundOffLedger) = _taxLedgerRepo.GetOrCreateDiscountRoundOffHeads(db, tx, voucher.Type);
            bool taxableIsDebit = IsTaxableDebit(voucher.Type);

            if (voucher.DiscountAmount != 0)
            {
                // Discount is always a contra to the taxable leg's own
                // direction — it shrinks the taxable/tax side, so it posts
                // opposite to whichever way Sales@X%/Purchase@X%/etc. went.
                double amt = Math.Abs(voucher.DiscountAmount);
                PostLeg(db, tx, voucher, series, 0, discountLedger, RoleDiscount,
                    debit: taxableIsDebit ? 0 : amt, credit: taxableIsDebit ? amt : 0);
            }

            if (voucher.RoundOffAmount != 0)
            {
                // A positive RoundOff added to the net total posts the SAME
                // direction as the taxable leg (extra income for Sales/
                // PurchaseReturn, extra expense for Purchase/SalesReturn);
                // a negative RoundOff (net total rounded down) posts the
                // opposite direction.
                double amt = Math.Abs(voucher.RoundOffAmount);
                bool roundOffIsDebit = voucher.RoundOffAmount > 0 ? taxableIsDebit : !taxableIsDebit;
                PostLeg(db, tx, voucher, series, 0, roundOffLedger, RoleRoundOff,
                    debit: roundOffIsDebit ? amt : 0, credit: roundOffIsDebit ? 0 : amt);
            }
        }

        // Same direction table as ResolveLedgers' taxableIsDebit column,
        // factored out so Discount/RoundOff can reuse it without needing a
        // TaxLedgerMapping (they don't route through tax_ledger_mapping at
        // all — they hit their own fixed-code heads instead).
        private bool IsTaxableDebit(TransactionType type)
        {
            switch (type)
            {
                case TransactionType.Sales: return false;
                case TransactionType.Purchase: return true;
                case TransactionType.SalesReturn: return true;
                case TransactionType.PurchaseReturn: return false;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        // Full rebuild path — used only by the maintenance RefreshDaybookSummary
        // entry point, never on the live Post/Reverse hot path. Reconstructs
        // both granular and grouped rows straight from voucher_tax_postings,
        // using the same IsGroupedRole rule (expressed in SQL here since
        // SettlementType is now persisted per posting row).
        private void RebuildDaybookSummaryFromPostings(IDbConnection db, IDbTransaction tx, string entryDate)
        {
            if (entryDate == null)
            {
                db.Execute("DELETE FROM daybook_summary", transaction: tx);
                db.Execute("DELETE FROM cash_transactions", transaction: tx);
            }
            else
            {
                db.Execute("DELETE FROM daybook_summary WHERE entry_date = @Date", new { Date = entryDate }, tx);
                db.Execute("DELETE FROM cash_transactions WHERE entry_date = @Date", new { Date = entryDate }, tx);
            }

            string dateFilter = entryDate == null ? "" : "AND entry_date = @Date";
            object dateParam = new { Date = entryDate };

            // Grouped-eligible predicate, shared by both queries below —
            // must mirror IsGroupedRole exactly: only the GST-construct
            // legs group; every Party row (Bank or actual customer/vendor
            // credit) stays granular, always. Cash is excluded from both —
            // it's rebuilt into cash_transactions separately below, never
            // daybook_summary — see PostLeg/InsertCashBookRow.
            const string groupedPredicate = @"
                transaction_type IN ('Sales','SalesReturn')
                AND ledger_role IN ('Taxable','Tax','Discount','RoundOff')";

            db.Execute($@"
                INSERT INTO daybook_summary (
                    transaction_type, entry_date, ledger_id, ledger_role, tax_percent, series,
                    voucher_ref, bill_no_from, bill_no_to, debit_amount, credit_amount, last_refreshed_date)
                SELECT transaction_type, entry_date, ledger_id, ledger_role, tax_percent, NULL,
                       voucher_ref, NULL, NULL, debit_amount, credit_amount, datetime('now')
                FROM voucher_tax_postings
                WHERE is_reversed = 0 {dateFilter}
                  AND NOT ({groupedPredicate})
                  AND (settlement_type IS NULL OR settlement_type != 'Cash')",
                dateParam, tx);

            db.Execute($@"
                INSERT INTO daybook_summary (
                    transaction_type, entry_date, ledger_id, ledger_role, tax_percent, series,
                    voucher_ref, bill_no_from, bill_no_to, debit_amount, credit_amount, last_refreshed_date)
                SELECT transaction_type, entry_date, ledger_id, ledger_role, tax_percent, series,
                       NULL, MIN(voucher_ref), MAX(voucher_ref), SUM(debit_amount), SUM(credit_amount), datetime('now')
                FROM voucher_tax_postings
                WHERE is_reversed = 0 {dateFilter}
                  AND ({groupedPredicate})
                GROUP BY transaction_type, entry_date, ledger_id, ledger_role, tax_percent, series",
                dateParam, tx);

            // Cash legs, rebuilt into their own table from the same audit
            // trail — always granular (one row per voucher), same shape as
            // InsertCashBookRow writes at live-posting time.
            db.Execute($@"
                INSERT INTO cash_transactions (
                    transaction_type, voucher_ref, entry_date, ledger_id, debit_amount, credit_amount, created_date)
                SELECT transaction_type, voucher_ref, entry_date, ledger_id, debit_amount, credit_amount, datetime('now')
                FROM voucher_tax_postings
                WHERE is_reversed = 0 {dateFilter}
                  AND settlement_type = 'Cash'",
                dateParam, tx);
        }
    }
}