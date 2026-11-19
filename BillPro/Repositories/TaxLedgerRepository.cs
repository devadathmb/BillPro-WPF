using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    public class TaxLedgerRepository
    {
        // Category all auto-created tax heads get filed under, so they
        // don't clutter the normal vendor/customer AcHead lookup combos.
        // AccountCategoryRepository already has AddCategory/CategoryExists —
        // reused here rather than duplicated.
        private const string TaxCategoryName = "Tax Ledgers";

        public TaxLedgerRepository()
        {
            InitializeDatabase();
        }

        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Execute(@"CREATE TABLE IF NOT EXISTS tax_ledger_mapping (
                                tax_percent REAL PRIMARY KEY,
                                sales_ledger_id INTEGER NOT NULL,
                                purchase_ledger_id INTEGER NOT NULL,
                                sales_return_ledger_id INTEGER NOT NULL,
                                purchase_return_ledger_id INTEGER NOT NULL,
                                tax_collected_ledger_id INTEGER NOT NULL,
                                tax_paid_ledger_id INTEGER NOT NULL);");

                db.Execute(@"CREATE TABLE IF NOT EXISTS voucher_tax_postings (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                transaction_type TEXT NOT NULL,
                                voucher_ref TEXT NOT NULL,
                                entry_date TEXT NOT NULL,
                                tax_percent REAL NOT NULL,
                                ledger_id INTEGER NOT NULL,
                                ledger_role TEXT NOT NULL,
                                debit_amount REAL NOT NULL DEFAULT 0,
                                credit_amount REAL NOT NULL DEFAULT 0,
                                is_reversed INTEGER NOT NULL DEFAULT 0,
                                created_date TEXT);");

                TryAddColumn(db, "voucher_tax_postings", "series TEXT");
                TryAddColumn(db, "voucher_tax_postings", "settlement_type TEXT");

                db.Execute(@"CREATE INDEX IF NOT EXISTS idx_voucher_tax_postings_ref
                             ON voucher_tax_postings (transaction_type, voucher_ref, is_reversed);");

                DropLegacyDaybookSummaryIfStale(db);

                db.Execute(@"CREATE TABLE IF NOT EXISTS daybook_summary (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                transaction_type TEXT NOT NULL,
                                entry_date TEXT NOT NULL,
                                ledger_id INTEGER NOT NULL,
                                ledger_role TEXT NOT NULL,
                                tax_percent REAL NOT NULL DEFAULT 0,
                                series TEXT,
                                voucher_ref TEXT,
                                bill_no_from TEXT,
                                bill_no_to TEXT,
                                debit_amount REAL NOT NULL DEFAULT 0,
                                credit_amount REAL NOT NULL DEFAULT 0,
                                last_refreshed_date TEXT);");

                db.Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_daybook_summary_grouped
                             ON daybook_summary (transaction_type, entry_date, series, tax_percent, ledger_role, ledger_id)
                             WHERE voucher_ref IS NULL;");

                db.Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_daybook_summary_granular
                             ON daybook_summary (transaction_type, voucher_ref, ledger_id, ledger_role, tax_percent)
                             WHERE voucher_ref IS NOT NULL;");

                db.Execute(@"CREATE INDEX IF NOT EXISTS idx_daybook_summary_date
                             ON daybook_summary (entry_date);");
                db.Execute(@"CREATE INDEX IF NOT EXISTS idx_daybook_summary_ledger
                             ON daybook_summary (ledger_id, entry_date);");

                db.Execute(@"CREATE TABLE IF NOT EXISTS cash_transactions (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                transaction_type TEXT NOT NULL,
                                voucher_ref TEXT NOT NULL,
                                entry_date TEXT NOT NULL,
                                ledger_id INTEGER NOT NULL,
                                debit_amount REAL NOT NULL DEFAULT 0,
                                credit_amount REAL NOT NULL DEFAULT 0,
                                created_date TEXT);");

                db.Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_cash_transactions_ref
                             ON cash_transactions (transaction_type, voucher_ref);");

                db.Execute(@"CREATE INDEX IF NOT EXISTS idx_cash_transactions_date
                             ON cash_transactions (entry_date);");

                DropLegacyOpeningBalanceIfStale(db);

                db.Execute(@"CREATE TABLE IF NOT EXISTS opening_cash_balance (
                                id INTEGER PRIMARY KEY CHECK (id = 1),
                                amount REAL NOT NULL DEFAULT 0,
                                updated_by TEXT,
                                updated_date TEXT);");
            }

            int cashLedgerId = GetOrCreateCashHeadId();

            MigrateLegacyCashRowsOutOfDaybookSummary(cashLedgerId);
        }

        private void MigrateLegacyCashRowsOutOfDaybookSummary(int cashLedgerId)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    var legacyRows = db.Query(
                        @"SELECT transaction_type, voucher_ref, entry_date, debit_amount, credit_amount
                          FROM daybook_summary WHERE ledger_id = @CashId",
                        new { CashId = cashLedgerId }, tx).AsList();

                    foreach (var r in legacyRows)
                    {
                        string voucherRef = (string)r.voucher_ref ?? $"LEGACY-{r.entry_date}-{Guid.NewGuid():N}";

                        db.Execute(@"INSERT OR IGNORE INTO cash_transactions (
                                        transaction_type, voucher_ref, entry_date, ledger_id,
                                        debit_amount, credit_amount, created_date)
                                     VALUES (@transaction_type, @VoucherRef, @entry_date, @CashId,
                                        @debit_amount, @credit_amount, datetime('now'))",
                            new
                            {
                                r.transaction_type,
                                VoucherRef = voucherRef,
                                r.entry_date,
                                CashId = cashLedgerId,
                                r.debit_amount,
                                r.credit_amount
                            }, tx);
                    }

                    db.Execute("DELETE FROM daybook_summary WHERE ledger_id = @CashId", new { CashId = cashLedgerId }, tx);

                    tx.Commit();
                }
            }
        }

        private void DropLegacyOpeningBalanceIfStale(IDbConnection db)
        {
            bool tableExists = db.QueryFirstOrDefault<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='opening_cash_balance'") > 0;

            if (!tableExists)
                return;

            bool hasAsOfDate = ((IEnumerable<dynamic>)db.Query("PRAGMA table_info(opening_cash_balance);"))
                .Select(row => (string)row.name)
                .Contains("as_of_date", StringComparer.OrdinalIgnoreCase);

            if (hasAsOfDate)
                db.Execute("DROP TABLE opening_cash_balance;");
        }

        // ════════════════════════════════════════════════════════════════
        // OPENING CASH BALANCE
        // ════════════════════════════════════════════════════════════════

        public OpeningCashBalance GetOpeningBalance()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.QueryFirstOrDefault<OpeningCashBalance>(
                    "SELECT amount AS Amount, updated_by AS UpdatedBy, updated_date AS UpdatedDate FROM opening_cash_balance WHERE id = 1")
                    ?? new OpeningCashBalance { Amount = 0, UpdatedDate = null };
            }
        }

        public void SetOpeningBalance(double amount, string currentUser)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Execute(@"INSERT OR REPLACE INTO opening_cash_balance (id, amount, updated_by, updated_date)
                             VALUES (1, @Amount, @User, @Now)",
                    new
                    {
                        Amount = amount,
                        User = currentUser,
                        Now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    });
            }
        }

        // ════════════════════════════════════════════════════════════════
        // LEDGER REPORT (INDIVIDUAL ACCOUNT HEAD)
        // ════════════════════════════════════════════════════════════════

        /// <summary>Every account head, for the Ledger Report's picker dropdown.</summary>
        public List<AcHeadPickerLookup> GetAcHeadPickerLookups()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AcHeadPickerLookup>(
                    @"SELECT id AS LedgerId, ac_head_name AS AcHeadName, ac_head_code AS AcHeadCode
                      FROM ac_head_master
                      ORDER BY ac_head_name COLLATE NOCASE").AsList();
            }
        }

        private class AcHeadOpening
        {
            public double OpeningCredit { get; set; }
            public double OpeningDebit { get; set; }
        }

        // Plain row shape for the raw daybook_summary query behind
        // GetLedgerHistory — pulled as separate bill_no_from/bill_no_to
        // fields (not pre-concatenated in SQL) so Narration can be built
        // in C# via BuildBillRangeLabel, same single-vs-range handling
        // GetDaybookEntries already uses. See GetLedgerHistory's own
        // comment for why the old inline SQL concatenation was wrong.
        private class RawLedgerHistoryRow
        {
            public long SortKey { get; set; }
            public string EntryDate { get; set; }
            public string TransactionType { get; set; }
            public string VoucherRef { get; set; }
            public string Series { get; set; }
            public string BillNoFrom { get; set; }
            public string BillNoTo { get; set; }
            public double DebitAmount { get; set; }
            public double CreditAmount { get; set; }
        }

        /// <summary>
        /// Full, unfiltered chronological history for a single account head
        /// — every daybook_summary row (grouped and granular alike) posted
        /// against it, oldest first, with a running balance computed here
        /// in C# as each row is walked (Credit - Debit, same sign
        /// convention as GetBalanceBefore). The view applies its own
        /// date/from-to filter on top of this for display, same pattern as
        /// IndividualStockReportView — the running balance has to be
        /// computed over the FULL history first, or a mid-range opening
        /// balance would be wrong.
        ///
        /// BUGFIX: this now folds in the account head's own
        /// ac_head_master.opening_credit/opening_debit as the starting
        /// baseline (Credit - Debit, same sign convention as everywhere
        /// else) — every head can carry its own opening balance
        /// independently of the daybook's overall opening_cash_balance,
        /// which only ever applies to postings, not to a head's
        /// pre-existing balance. Previously the running balance always
        /// started from 0, silently ignoring whatever was entered on the
        /// account head's own master record. When a head does have an
        /// opening balance configured, it's also surfaced as an actual
        /// synthetic row (dated far enough in the past — "0001-01-01" —
        /// that it's always picked up as "before" any realistic report
        /// from-date), so it's visible and traceable rather than just a
        /// silent adjustment baked into the running total.
        ///
        /// BUGFIX 2: Narration for a grouped row is now built in C# via
        /// BuildBillRangeLabel instead of the old inline SQL
        /// "bill_no_from || ' - ' || bill_no_to" — that always showed a
        /// range even when only one bill posted to the bucket (e.g.
        /// "B-1-26-27 - B-1-26-27"), unlike the Daybook Report, which
        /// already collapses that to a single "B-1-26-27" via
        /// BuildBillRangeLabel. This brings the Ledger Report's narration
        /// in line with the Daybook's.
        /// </summary>
        public List<LedgerTransactionReportRow> GetLedgerHistory(int ledgerId)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var opening = db.QueryFirstOrDefault<AcHeadOpening>(
                    "SELECT opening_credit AS OpeningCredit, opening_debit AS OpeningDebit FROM ac_head_master WHERE id = @Id",
                    new { Id = ledgerId }) ?? new AcHeadOpening();

                double baseline = opening.OpeningCredit - opening.OpeningDebit;

                var rawRows = db.Query<RawLedgerHistoryRow>(
                    @"SELECT id AS SortKey,
                             entry_date AS EntryDate,
                             transaction_type AS TransactionType,
                             voucher_ref AS VoucherRef,
                             series AS Series,
                             bill_no_from AS BillNoFrom,
                             bill_no_to AS BillNoTo,
                             debit_amount AS DebitAmount,
                             credit_amount AS CreditAmount
                      FROM daybook_summary
                      WHERE ledger_id = @LedgerId
                      ORDER BY entry_date, id",
                    new { LedgerId = ledgerId }).AsList();

                var rows = rawRows.Select(r => new LedgerTransactionReportRow
                {
                    SortKey = r.SortKey,
                    EntryDate = r.EntryDate,
                    TransactionType = r.TransactionType,
                    Narration = r.VoucherRef ?? BuildBillRangeLabel(r.BillNoFrom, r.BillNoTo),
                    VoucherRef = r.VoucherRef,
                    Series = r.Series,
                    BillNoFrom = r.BillNoFrom,
                    BillNoTo = r.BillNoTo,
                    DebitAmount = r.DebitAmount,
                    CreditAmount = r.CreditAmount
                }).ToList();

                double running = baseline;
                foreach (var row in rows)
                {
                    running += row.CreditAmount - row.DebitAmount;
                    row.RunningBalance = running;
                }

                if (opening.OpeningCredit != 0 || opening.OpeningDebit != 0)
                {
                    rows.Insert(0, new LedgerTransactionReportRow
                    {
                        SortKey = -1,
                        EntryDate = "0001-01-01",
                        TransactionType = "Opening Balance",
                        Narration = "Account Opening Balance",
                        DebitAmount = opening.OpeningDebit,
                        CreditAmount = opening.OpeningCredit,
                        RunningBalance = baseline
                    });
                }

                return rows;
            }
        }

        private void TryAddColumn(IDbConnection db, string tableName, string columnDefinition)
        {
            try
            {
                db.Execute($"ALTER TABLE {tableName} ADD COLUMN {columnDefinition};");
            }
            catch { /* column already exists */ }
        }

        private bool DropLegacyDaybookSummaryIfStale(IDbConnection db)
        {
            bool tableExists = db.QueryFirstOrDefault<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='daybook_summary'") > 0;

            if (!tableExists)
                return false;

            var columnNames = ((IEnumerable<dynamic>)db.Query("PRAGMA table_info(daybook_summary);"))
                .Select(row => (string)row.name)
                .ToList();

            bool hasCurrentShape = columnNames.Contains("series", StringComparer.OrdinalIgnoreCase)
                                    && columnNames.Contains("voucher_ref", StringComparer.OrdinalIgnoreCase)
                                    && columnNames.Contains("bill_no_from", StringComparer.OrdinalIgnoreCase);

            if (hasCurrentShape)
                return false;

            db.Execute("DROP TABLE daybook_summary;");
            return true;
        }

        public bool MigrateLegacyDaybookSummaryIfNeeded()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
                return DropLegacyDaybookSummaryIfStale(db);
        }

        public TaxLedgerMapping GetOrCreateMapping(IDbConnection db, IDbTransaction tx, double taxPercent)
        {
            var existing = db.QueryFirstOrDefault<TaxLedgerMapping>(
                @"SELECT tax_percent AS TaxPercent, sales_ledger_id AS SalesLedgerId,
                         purchase_ledger_id AS PurchaseLedgerId, sales_return_ledger_id AS SalesReturnLedgerId,
                         purchase_return_ledger_id AS PurchaseReturnLedgerId,
                         tax_collected_ledger_id AS TaxCollectedLedgerId, tax_paid_ledger_id AS TaxPaidLedgerId
                  FROM tax_ledger_mapping WHERE tax_percent = @TaxPercent",
                new { TaxPercent = taxPercent }, tx);

            if (existing != null) return existing;

            int categoryCode = GetOrCreateTaxCategory(db, tx);

            string label = taxPercent % 1 == 0 ? $"{(int)taxPercent}%" : $"{taxPercent}%";

            var mapping = new TaxLedgerMapping
            {
                TaxPercent = taxPercent,
                SalesLedgerId = CreateHead(db, tx, $"Sales @ {label}", categoryCode, SalesSeriesBase, acGroupId: 1),
                PurchaseLedgerId = CreateHead(db, tx, $"Purchase @ {label}", categoryCode, PurchaseSeriesBase, acGroupId: 1),
                SalesReturnLedgerId = CreateHead(db, tx, $"Sales Return @ {label}", categoryCode, SalesReturnSeriesBase, acGroupId: 1),
                PurchaseReturnLedgerId = CreateHead(db, tx, $"Purchase Return @ {label}", categoryCode, PurchaseReturnSeriesBase, acGroupId: 1),
                TaxCollectedLedgerId = CreateHead(db, tx, $"Tax Collected @ {label}", categoryCode, TaxCollectedSeriesBase, acGroupId: 2),
                TaxPaidLedgerId = CreateHead(db, tx, $"Tax Paid @ {label}", categoryCode, TaxPaidSeriesBase, acGroupId: 2)
            };

            db.Execute(@"INSERT INTO tax_ledger_mapping (
                            tax_percent, sales_ledger_id, purchase_ledger_id, sales_return_ledger_id,
                            purchase_return_ledger_id, tax_collected_ledger_id, tax_paid_ledger_id)
                         VALUES (
                            @TaxPercent, @SalesLedgerId, @PurchaseLedgerId, @SalesReturnLedgerId,
                            @PurchaseReturnLedgerId, @TaxCollectedLedgerId, @TaxPaidLedgerId)",
                mapping, tx);

            return mapping;
        }

        private int GetOrCreateTaxCategory(IDbConnection db, IDbTransaction tx)
        {
            var code = db.QueryFirstOrDefault<int?>(
                "SELECT category_code FROM ac_category_master WHERE category_name = @Name",
                new { Name = TaxCategoryName }, tx);
            if (code.HasValue) return code.Value;

            int maxCode = db.ExecuteScalar<int?>("SELECT MAX(category_code) FROM ac_category_master", transaction: tx) ?? 0;
            int newCode = maxCode < 101 ? 101 : maxCode + 1;

            db.Execute(@"INSERT INTO ac_category_master (category_code, category_name, created_date)
                         VALUES (@Code, @Name, datetime('now'))",
                new { Code = newCode, Name = TaxCategoryName }, tx);

            return newCode;
        }

        private const int SalesSeriesBase = 400;
        private const int PurchaseSeriesBase = 500;
        private const int SalesReturnSeriesBase = 600;
        private const int PurchaseReturnSeriesBase = 700;
        private const int TaxCollectedSeriesBase = 800;
        private const int TaxPaidSeriesBase = 900;

        private int CreateHead(IDbConnection db, IDbTransaction tx, string headName, int categoryCode, int seriesBase, int acGroupId)
        {
            var existingId = db.QueryFirstOrDefault<int?>(
                "SELECT id FROM ac_head_master WHERE ac_head_name = @Name", new { Name = headName }, tx);
            if (existingId.HasValue) return existingId.Value;

            string code = GetNextCodeInSeries(db, tx, seriesBase);

            db.Execute(@"INSERT INTO ac_head_master (ac_head_code, ac_head_name, ac_category_id, ac_group_id)
                         VALUES (@Code, @Name, @CategoryCode, @AcGroupId)",
                new { Code = code, Name = headName, CategoryCode = categoryCode, AcGroupId = acGroupId }, tx);

            return (int)db.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
        }

        private int CreateFixedCodeHead(IDbConnection db, IDbTransaction tx, string headName, int categoryCode, int fixedCode, int acGroupId)
        {
            var existingId = db.QueryFirstOrDefault<int?>(
                "SELECT id FROM ac_head_master WHERE ac_head_name = @Name", new { Name = headName }, tx);
            if (existingId.HasValue) return existingId.Value;

            db.Execute(@"INSERT INTO ac_head_master (ac_head_code, ac_head_name, ac_category_id, ac_group_id)
                         VALUES (@Code, @Name, @CategoryCode, @AcGroupId)",
                new { Code = fixedCode.ToString(), Name = headName, CategoryCode = categoryCode, AcGroupId = acGroupId }, tx);

            return (int)db.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
        }

        public (int DiscountLedgerId, int RoundOffLedgerId) GetOrCreateDiscountRoundOffHeads(
            IDbConnection db, IDbTransaction tx, TransactionType type)
        {
            int categoryCode = GetOrCreateTaxCategory(db, tx);

            (string label, int discountCode, int roundOffCode) = type switch
            {
                TransactionType.Sales => ("Sales", SalesSeriesBase + 50, SalesSeriesBase + 51),
                TransactionType.Purchase => ("Purchase", PurchaseSeriesBase + 50, PurchaseSeriesBase + 51),
                TransactionType.SalesReturn => ("Sales Return", SalesReturnSeriesBase + 50, SalesReturnSeriesBase + 51),
                TransactionType.PurchaseReturn => ("Purchase Return", PurchaseReturnSeriesBase + 50, PurchaseReturnSeriesBase + 51),
                _ => throw new ArgumentOutOfRangeException(nameof(type), $"No Discount/Round Off heads defined for {type}")
            };

            int discountId = CreateFixedCodeHead(db, tx, $"{label} Discount", categoryCode, discountCode, acGroupId: 1);
            int roundOffId = CreateFixedCodeHead(db, tx, $"{label} Round Off", categoryCode, roundOffCode, acGroupId: 1);
            return (discountId, roundOffId);
        }

        private const int ReceiptPaymentSeriesBase = 350;

        public int GetOrCreateDiscountAllowedHead(IDbConnection db, IDbTransaction tx)
        {
            int categoryCode = GetOrCreateTaxCategory(db, tx);
            return CreateFixedCodeHead(db, tx, "Discount Allowed", categoryCode, ReceiptPaymentSeriesBase + 1, acGroupId: 1);
        }

        private string GetNextCodeInSeries(IDbConnection db, IDbTransaction tx, int seriesBase)
        {
            int lo = seriesBase + 1;
            int hi = seriesBase + 99;

            int? maxInSeries = db.ExecuteScalar<int?>(
                @"SELECT MAX(CAST(ac_head_code AS INTEGER)) FROM ac_head_master
                  WHERE ac_head_code GLOB '[0-9]*'
                    AND CAST(ac_head_code AS INTEGER) BETWEEN @Lo AND @Hi",
                new { Lo = lo, Hi = hi }, tx);

            int next = maxInSeries.HasValue ? maxInSeries.Value + 1 : lo;
            return next.ToString();
        }

        // ── Reporting ───────────────────────────────────────────────────

        private class LedgerSum
        {
            public int LedgerId { get; set; }
            public double Debit { get; set; }
            public double Credit { get; set; }
        }

        public List<DaybookReportRow> GetTaxwiseSummary(DateTime fromDate, DateTime toDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var mappings = db.Query<TaxLedgerMapping>(
                    @"SELECT tax_percent AS TaxPercent, sales_ledger_id AS SalesLedgerId,
                             purchase_ledger_id AS PurchaseLedgerId, sales_return_ledger_id AS SalesReturnLedgerId,
                             purchase_return_ledger_id AS PurchaseReturnLedgerId,
                             tax_collected_ledger_id AS TaxCollectedLedgerId, tax_paid_ledger_id AS TaxPaidLedgerId
                      FROM tax_ledger_mapping ORDER BY tax_percent").AsList();

                if (mappings.Count == 0) return new List<DaybookReportRow>();

                var sumRows = db.Query<LedgerSum>(
                    @"SELECT ledger_id AS LedgerId, SUM(debit_amount) AS Debit, SUM(credit_amount) AS Credit
                      FROM daybook_summary
                      WHERE entry_date BETWEEN @From AND @To
                      GROUP BY ledger_id",
                    new { From = fromDate.ToString("yyyy-MM-dd"), To = toDate.ToString("yyyy-MM-dd") }).AsList();

                var sums = sumRows.ToDictionary(s => s.LedgerId, s => s.Debit + s.Credit);

                double Amt(int ledgerId) => sums.TryGetValue(ledgerId, out var v) ? v : 0;

                return mappings
                    .Select(m => new DaybookReportRow
                    {
                        TaxPercent = m.TaxPercent,
                        SalesAmount = Amt(m.SalesLedgerId),
                        PurchaseAmount = Amt(m.PurchaseLedgerId),
                        SalesReturnAmount = Amt(m.SalesReturnLedgerId),
                        PurchaseReturnAmount = Amt(m.PurchaseReturnLedgerId),
                        TaxCollected = Amt(m.TaxCollectedLedgerId),
                        TaxPaid = Amt(m.TaxPaidLedgerId)
                    })
                    .Where(r => r.SalesAmount != 0 || r.PurchaseAmount != 0 || r.SalesReturnAmount != 0
                             || r.PurchaseReturnAmount != 0 || r.TaxCollected != 0 || r.TaxPaid != 0)
                    .ToList();
            }
        }

        private class RawDaybookSummaryRow
        {
            public long Id { get; set; }
            public string EntryDate { get; set; }
            public string AcHeadName { get; set; }
            public string TransactionType { get; set; }
            public string VoucherRef { get; set; }
            public string Series { get; set; }
            public string BillNoFrom { get; set; }
            public string BillNoTo { get; set; }
            public double DebitAmount { get; set; }
            public double CreditAmount { get; set; }
        }

        public List<DaybookLedgerRow> GetDaybookEntries(DateTime fromDate, DateTime toDate)
        {
            int cashLedgerId = GetOrCreateCashHeadId();

            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");

                var rawRows = db.Query<RawDaybookSummaryRow>(
                    @"SELECT ds.id AS Id, ds.entry_date AS EntryDate, ah.ac_head_name AS AcHeadName,
                             ds.transaction_type AS TransactionType, ds.voucher_ref AS VoucherRef,
                             ds.series AS Series, ds.bill_no_from AS BillNoFrom, ds.bill_no_to AS BillNoTo,
                             ds.debit_amount AS DebitAmount, ds.credit_amount AS CreditAmount
                      FROM daybook_summary ds
                      JOIN ac_head_master ah ON ah.id = ds.ledger_id
                      WHERE ds.entry_date BETWEEN @From AND @To
                        AND (ds.debit_amount != 0 OR ds.credit_amount != 0)
                        AND ds.ledger_id != @CashLedgerId",
                    new { From = from, To = to, CashLedgerId = cashLedgerId }).AsList();

                var allRows = rawRows
                    .Select(r =>
                    {
                        // A grouped bucket row (Sales/SalesReturn Taxable/Tax/
                        // Discount/RoundOff — see UpsertGroupedDaybookRow) can
                        // net negative on either side once enough cancels/
                        // returns/Updates have added and subtracted against
                        // it (e.g. a bucket that's mostly reversals). Rather
                        // than print "-299.00" under Payment, fold it onto
                        // the opposite column as its positive magnitude —
                        // same convention as flipping a debit balance to
                        // "Dr"/"Cr" instead of showing a sign. Day Total
                        // below sums these already-normalized figures, so
                        // the day's totals stay correct too.
                        double debit = r.DebitAmount;
                        double credit = r.CreditAmount;

                        if (debit < 0) { credit -= debit; debit = 0; }
                        if (credit < 0) { debit -= credit; credit = 0; }

                        return new DaybookLedgerRow
                        {
                            EntryDate = r.EntryDate,
                            AcHeadName = r.AcHeadName,
                            TransactionType = r.TransactionType,
                            VoucherRef = r.VoucherRef ?? BuildBillRangeLabel(r.BillNoFrom, r.BillNoTo),
                            GroupSeries = r.VoucherRef == null ? r.Series : null,
                            GroupBillNoFrom = r.VoucherRef == null ? r.BillNoFrom : null,
                            GroupBillNoTo = r.VoucherRef == null ? r.BillNoTo : null,
                            DebitAmount = debit,
                            CreditAmount = credit,
                            SortKey = r.Id
                        };
                    })
                    .OrderBy(r => r.EntryDate)
                    .ThenBy(r => r.SortKey)
                    .ToList();

                double runningBalance = GetBalanceBefore(db, from);

                var result = new List<DaybookLedgerRow>();
                foreach (var dayGroup in allRows.GroupBy(r => r.EntryDate).OrderBy(g => g.Key))
                {
                    var dayRows = dayGroup.OrderBy(r => r.SortKey).ToList();

                    result.Add(BuildDateHeaderRow(dayGroup.Key));
                    result.Add(BuildOpeningBalanceRow(dayGroup.Key, runningBalance));
                    result.AddRange(dayRows);

                    result.Add(BuildDayTotalRow(dayGroup.Key,
                        receiptTotal: dayRows.Sum(r => r.CreditAmount),
                        paymentTotal: dayRows.Sum(r => r.DebitAmount)));

                    runningBalance += dayRows.Sum(r => r.CreditAmount - r.DebitAmount);
                }

                return result;
            }
        }

        private class RawCashTransactionRow
        {
            public long Id { get; set; }
            public string EntryDate { get; set; }
            public string TransactionType { get; set; }
            public string VoucherRef { get; set; }
            public double DebitAmount { get; set; }
            public double CreditAmount { get; set; }
        }

        public List<CashbookLedgerRow> GetCashbookEntries(DateTime fromDate, DateTime toDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");

                var rawRows = db.Query<RawCashTransactionRow>(
                    @"SELECT id AS Id, entry_date AS EntryDate, transaction_type AS TransactionType,
                             voucher_ref AS VoucherRef, debit_amount AS DebitAmount, credit_amount AS CreditAmount
                      FROM cash_transactions
                      WHERE entry_date BETWEEN @From AND @To
                        AND (debit_amount != 0 OR credit_amount != 0)",
                    new { From = from, To = to }).AsList();

                var allRows = rawRows
                    .Select(r => new CashbookLedgerRow
                    {
                        EntryDate = r.EntryDate,
                        TransactionType = r.TransactionType,
                        VoucherRef = r.VoucherRef,
                        DebitAmount = r.DebitAmount,
                        CreditAmount = r.CreditAmount,
                        SortKey = r.Id
                    })
                    .OrderBy(r => r.EntryDate)
                    .ThenBy(r => r.SortKey)
                    .ToList();

                double runningBalance = GetCashBalanceBefore(db, from);

                var result = new List<CashbookLedgerRow>();
                foreach (var dayGroup in allRows.GroupBy(r => r.EntryDate).OrderBy(g => g.Key))
                {
                    var dayRows = dayGroup.OrderBy(r => r.SortKey).ToList();

                    result.Add(BuildCashbookDateHeaderRow(dayGroup.Key));
                    result.Add(BuildCashbookOpeningBalanceRow(dayGroup.Key, runningBalance));
                    result.AddRange(dayRows);

                    result.Add(BuildCashbookDayTotalRow(dayGroup.Key,
                        receiptTotal: dayRows.Sum(r => r.DebitAmount),
                        paymentTotal: dayRows.Sum(r => r.CreditAmount)));

                    runningBalance += dayRows.Sum(r => r.DebitAmount - r.CreditAmount);
                }

                return result;
            }
        }

        private double GetCashBalanceBefore(IDbConnection db, string fromDate)
        {
            var opening = GetOpeningBalance();

            var totals = db.QueryFirstOrDefault<BalanceTotals>(
                @"SELECT SUM(debit_amount) AS Debit, SUM(credit_amount) AS Credit
                  FROM cash_transactions
                  WHERE entry_date < @From",
                new { From = fromDate });

            double postingsBalance = ((totals?.Debit) ?? 0) - ((totals?.Credit) ?? 0);

            return opening.Amount + postingsBalance;
        }

        public double GetCashBookClosingBalance(DateTime asOfDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return GetCashBalanceBefore(db, asOfDate.AddDays(1).ToString("yyyy-MM-dd"));
            }
        }

        private CashbookLedgerRow BuildCashbookDateHeaderRow(string entryDate)
        {
            return new CashbookLedgerRow
            {
                EntryDate = entryDate,
                TransactionType = "",
                VoucherRef = "",
                SortKey = -2,
                IsDateHeader = true
            };
        }

        private CashbookLedgerRow BuildCashbookOpeningBalanceRow(string entryDate, double balance)
        {
            return new CashbookLedgerRow
            {
                EntryDate = entryDate,
                TransactionType = "Balance b/f",
                VoucherRef = "",
                DebitAmount = balance,
                CreditAmount = 0,
                SortKey = -1,
                IsOpeningBalance = true
            };
        }

        private CashbookLedgerRow BuildCashbookDayTotalRow(string entryDate, double receiptTotal, double paymentTotal)
        {
            return new CashbookLedgerRow
            {
                EntryDate = entryDate,
                TransactionType = "",
                VoucherRef = "",
                SortKey = long.MaxValue,
                IsDayTotal = true,
                DayTotalReceipt = receiptTotal,
                DayTotalPayment = paymentTotal
            };
        }

        private string BuildBillRangeLabel(string billNoFrom, string billNoTo)
        {
            if (string.IsNullOrEmpty(billNoFrom)) return "";
            return billNoFrom == billNoTo ? billNoFrom : $"{billNoFrom} - {billNoTo}";
        }

        private class BalanceTotals
        {
            public double? Debit { get; set; }
            public double? Credit { get; set; }
        }

        private double GetBalanceBefore(IDbConnection db, string fromDate)
        {
            var opening = GetOpeningBalance();

            var totals = db.QueryFirstOrDefault<BalanceTotals>(
                @"SELECT SUM(debit_amount) AS Debit, SUM(credit_amount) AS Credit
                  FROM daybook_summary
                  WHERE entry_date < @From",
                new { From = fromDate });

            double postingsBalance = ((totals?.Credit) ?? 0) - ((totals?.Debit) ?? 0);

            return opening.Amount + postingsBalance;
        }

        private DaybookLedgerRow BuildDateHeaderRow(string entryDate)
        {
            return new DaybookLedgerRow
            {
                EntryDate = entryDate,
                AcHeadName = "",
                TransactionType = "DateHeader",
                VoucherRef = "",
                SortKey = -2,
                IsDateHeader = true
            };
        }

        private DaybookLedgerRow BuildOpeningBalanceRow(string entryDate, double balance)
        {
            return new DaybookLedgerRow
            {
                EntryDate = entryDate,
                AcHeadName = "Balance b/f",
                TransactionType = "OpeningBalance",
                VoucherRef = "",
                DebitAmount = 0,
                CreditAmount = balance,
                SortKey = -1,
                IsOpeningBalance = true
            };
        }

        private DaybookLedgerRow BuildDayTotalRow(string entryDate, double receiptTotal, double paymentTotal)
        {
            return new DaybookLedgerRow
            {
                EntryDate = entryDate,
                AcHeadName = "",
                TransactionType = "DayTotal",
                VoucherRef = "",
                SortKey = long.MaxValue,
                IsDayTotal = true,
                DayTotalReceipt = receiptTotal,
                DayTotalPayment = paymentTotal
            };
        }

        // ════════════════════════════════════════════════════════════════
        // LEDGER REPORT (INDIVIDUAL HEADS) — legacy picker/entries pair
        // ════════════════════════════════════════════════════════════════

        public List<AcHeadPickerLookup> GetAcHeadPickerList()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return db.Query<AcHeadPickerLookup>(
                    @"SELECT id AS Id, ac_head_name AS Name, ac_head_code AS Code
                      FROM ac_head_master
                      ORDER BY ac_head_name").AsList();
            }
        }

        private class RawLedgerRow
        {
            public long Id { get; set; }
            public string EntryDate { get; set; }
            public string TransactionType { get; set; }
            public string VoucherRef { get; set; }
            public string BillNoFrom { get; set; }
            public string BillNoTo { get; set; }
            public double DebitAmount { get; set; }
            public double CreditAmount { get; set; }
        }

        public List<LedgerReportRow> GetLedgerEntries(int ledgerId, DateTime fromDate, DateTime toDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");

                var rawRows = IsCashLedger(db, ledgerId)
                    ? db.Query<RawLedgerRow>(
                        @"SELECT id AS Id, entry_date AS EntryDate, transaction_type AS TransactionType,
                                 voucher_ref AS VoucherRef, NULL AS BillNoFrom, NULL AS BillNoTo,
                                 debit_amount AS DebitAmount, credit_amount AS CreditAmount
                          FROM cash_transactions
                          WHERE entry_date BETWEEN @From AND @To
                            AND (debit_amount != 0 OR credit_amount != 0)",
                        new { From = from, To = to }).AsList()
                    : db.Query<RawLedgerRow>(
                        @"SELECT id AS Id, entry_date AS EntryDate, transaction_type AS TransactionType,
                                 voucher_ref AS VoucherRef, bill_no_from AS BillNoFrom, bill_no_to AS BillNoTo,
                                 debit_amount AS DebitAmount, credit_amount AS CreditAmount
                          FROM daybook_summary
                          WHERE ledger_id = @LedgerId
                            AND entry_date BETWEEN @From AND @To
                            AND (debit_amount != 0 OR credit_amount != 0)",
                        new { LedgerId = ledgerId, From = from, To = to }).AsList();

                var rows = rawRows
                    .OrderBy(r => r.EntryDate)
                    .ThenBy(r => r.Id)
                    .Select(r => new LedgerReportRow
                    {
                        EntryDate = r.EntryDate,
                        TransactionType = r.TransactionType,
                        Narration = r.VoucherRef ?? BuildBillRangeLabel(r.BillNoFrom, r.BillNoTo),
                        DebitAmount = r.DebitAmount,
                        CreditAmount = r.CreditAmount,
                        SortKey = r.Id
                    })
                    .ToList();

                double running = GetLedgerBalanceBefore(db, ledgerId, from);

                var result = new List<LedgerReportRow> { BuildLedgerOpeningRow(from, running) };

                foreach (var r in rows)
                {
                    running += r.CreditAmount - r.DebitAmount;
                    r.RunningBalance = running;
                    result.Add(r);
                }

                return result;
            }
        }

        private double GetLedgerBalanceBefore(IDbConnection db, int ledgerId, string fromDate)
        {
            bool isCash = IsCashLedger(db, ledgerId);

            var totals = isCash
                ? db.QueryFirstOrDefault<BalanceTotals>(
                    @"SELECT SUM(debit_amount) AS Debit, SUM(credit_amount) AS Credit
                      FROM cash_transactions
                      WHERE entry_date < @From",
                    new { From = fromDate })
                : db.QueryFirstOrDefault<BalanceTotals>(
                    @"SELECT SUM(debit_amount) AS Debit, SUM(credit_amount) AS Credit
                      FROM daybook_summary
                      WHERE ledger_id = @LedgerId AND entry_date < @From",
                    new { LedgerId = ledgerId, From = fromDate });

            double postingsBalance = ((totals?.Credit) ?? 0) - ((totals?.Debit) ?? 0);
            double openingCash = isCash ? GetOpeningBalance().Amount : 0;

            return openingCash + postingsBalance;
        }

        private bool IsCashLedger(IDbConnection db, int ledgerId)
        {
            return db.QueryFirstOrDefault<int>(
                "SELECT COUNT(*) FROM ac_head_master WHERE id = @Id AND ac_head_name = 'Cash' COLLATE NOCASE",
                new { Id = ledgerId }) > 0;
        }

        // ════════════════════════════════════════════════════════════════
        // DEFAULT / SYSTEM HEADS — ac_head_code 1-100 reserved
        // ════════════════════════════════════════════════════════════════
        private const string DefaultHeadsCategoryName = "Default";
        private const string CashHeadCode = "1";

        private int GetOrCreateDefaultHeadsCategory(IDbConnection db, IDbTransaction tx)
        {
            var code = db.QueryFirstOrDefault<int?>(
                "SELECT category_code FROM ac_category_master WHERE category_name = @Name",
                new { Name = DefaultHeadsCategoryName }, tx);
            if (code.HasValue) return code.Value;

            int maxCode = db.ExecuteScalar<int?>("SELECT MAX(category_code) FROM ac_category_master", transaction: tx) ?? 0;
            int newCode = maxCode < 101 ? 101 : maxCode + 1;

            db.Execute(@"INSERT INTO ac_category_master (category_code, category_name, created_date)
                         VALUES (@Code, @Name, datetime('now'))",
                new { Code = newCode, Name = DefaultHeadsCategoryName }, tx);

            return newCode;
        }

        public int GetOrCreateCashHeadId()
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                db.Open();
                using (var tx = db.BeginTransaction())
                {
                    var existingId = db.QueryFirstOrDefault<int?>(
                        "SELECT id FROM ac_head_master WHERE ac_head_name = 'Cash' COLLATE NOCASE", transaction: tx);

                    if (existingId.HasValue)
                    {
                        tx.Commit();
                        return existingId.Value;
                    }

                    int categoryCode = GetOrCreateDefaultHeadsCategory(db, tx);

                    db.Execute(@"INSERT INTO ac_head_master (ac_head_code, ac_head_name, ac_category_id)
                                 VALUES (@Code, 'Cash', @CategoryCode)",
                        new { Code = CashHeadCode, CategoryCode = categoryCode }, tx);

                    int newId = (int)db.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
                    tx.Commit();
                    return newId;
                }
            }
        }

        public double GetLedgerBalanceAsOf(int ledgerId, DateTime asOfDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return GetLedgerBalanceBefore(db, ledgerId, asOfDate.AddDays(1).ToString("yyyy-MM-dd"));
            }
        }

        public double GetCashInHandBalance(DateTime asOfDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                return GetBalanceBefore(db, asOfDate.AddDays(1).ToString("yyyy-MM-dd"));
            }
        }

        // ════════════════════════════════════════════════════════════════
        // TRIAL BALANCE
        // ════════════════════════════════════════════════════════════════
        private class RawTrialBalanceRow
        {
            public int LedgerId { get; set; }
            public string AcHeadCode { get; set; }
            public string AcHeadName { get; set; }
            public double OpeningDebit { get; set; }
            public double OpeningCredit { get; set; }
            public double PostedDebit { get; set; }
            public double PostedCredit { get; set; }
        }

        public List<TrialBalanceRow> GetTrialBalance(DateTime toDate)
        {
            int cashLedgerId = GetOrCreateCashHeadId();

            double cashNet = GetCashInHandBalance(toDate);

            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var to = toDate.ToString("yyyy-MM-dd");

                var raw = db.Query<RawTrialBalanceRow>(
                    @"SELECT h.id AS LedgerId,
                             h.ac_head_code AS AcHeadCode,
                             h.ac_head_name AS AcHeadName,
                             h.opening_debit AS OpeningDebit,
                             h.opening_credit AS OpeningCredit,
                             COALESCE(SUM(d.debit_amount), 0.0) AS PostedDebit,
                             COALESCE(SUM(d.credit_amount), 0.0) AS PostedCredit
                      FROM ac_head_master h
                      LEFT JOIN daybook_summary d
                             ON d.ledger_id = h.id AND d.entry_date <= @To
                      WHERE COALESCE(h.ac_category_id, 0) <> @ClosingCat
                      GROUP BY h.id, h.ac_head_code, h.ac_head_name, h.opening_debit, h.opening_credit
                      ORDER BY h.ac_head_name",
                    new
                    {
                        To = to,
                        ClosingCat = AccountCategoryRepository.ClosingStockCategoryCode
                    }).AsList();

                var rows = new List<TrialBalanceRow>();

                foreach (var r in raw)
                {
                    bool isCashRow = r.LedgerId == cashLedgerId;

                    double net = isCashRow
                        ? cashNet
                        : (r.OpeningDebit - r.OpeningCredit) + r.PostedDebit - r.PostedCredit;

                    if (System.Math.Abs(net) < 0.005) continue;

                    rows.Add(new TrialBalanceRow
                    {
                        LedgerId = r.LedgerId,
                        AcHeadCode = r.AcHeadCode,
                        AcHeadName = r.AcHeadName,
                        Debit = net > 0 ? net : 0,
                        Credit = net < 0 ? -net : 0
                    });
                }

                return rows;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // BALANCE SHEET & TRADING / PROFIT & LOSS
        // ════════════════════════════════════════════════════════════════
        private static DateTime GetFinancialYearStart(DateTime asAt)
        {
            int fyStartYear = asAt.Month >= 4 ? asAt.Year : asAt.Year - 1;
            return new DateTime(fyStartYear, 4, 1);
        }

        public double GetNetProfitForPeriod(DateTime fromDate, DateTime toDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");

                var totals = db.QueryFirstOrDefault<(double Debit, double Credit)>(
                    @"SELECT COALESCE(SUM(d.debit_amount), 0.0)  AS Debit,
                             COALESCE(SUM(d.credit_amount), 0.0) AS Credit
                      FROM ac_head_master h
                      INNER JOIN daybook_summary d
                              ON d.ledger_id = h.id AND d.entry_date BETWEEN @From AND @To
                      WHERE COALESCE(h.ac_group_id, 0) <> 0
                        AND COALESCE(h.ac_category_id, 0) NOT IN (@OpeningCat, @ClosingCat)",
                    new
                    {
                        From = from,
                        To = to,
                        OpeningCat = AccountCategoryRepository.OpeningStockCategoryCode,
                        ClosingCat = AccountCategoryRepository.ClosingStockCategoryCode
                    });

                double postedNet = totals.Credit - totals.Debit;

                var stockHeads = db.Query<RawStockCategoryRow>(
                    @"SELECT ac_head_name AS AcHeadName, ac_category_id AS AcCategoryId,
                             opening_debit AS OpeningDebit, opening_credit AS OpeningCredit
                      FROM ac_head_master
                      WHERE ac_category_id IN (@OpeningCat, @ClosingCat)",
                    new
                    {
                        OpeningCat = AccountCategoryRepository.OpeningStockCategoryCode,
                        ClosingCat = AccountCategoryRepository.ClosingStockCategoryCode
                    }).AsList();

                double stockAdjustment = 0;
                foreach (var h in stockHeads)
                {
                    double value = GetStockHeadValue(h);
                    stockAdjustment += h.AcCategoryId == AccountCategoryRepository.OpeningStockCategoryCode
                        ? -value
                        : value;
                }

                return postedNet + stockAdjustment;
            }
        }

        private class RawProfitLossRow
        {
            public int LedgerId { get; set; }
            public string AcHeadName { get; set; }
            public int AcGroupId { get; set; }
            public double PostedDebit { get; set; }
            public double PostedCredit { get; set; }
        }

        public List<ProfitLossRow> GetTradingProfitLoss(DateTime fromDate, DateTime toDate)
        {
            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");

                var raw = db.Query<RawProfitLossRow>(
                    @"SELECT h.id AS LedgerId,
                             h.ac_head_name AS AcHeadName,
                             COALESCE(h.ac_group_id, 0) AS AcGroupId,
                             COALESCE(SUM(d.debit_amount), 0.0) AS PostedDebit,
                             COALESCE(SUM(d.credit_amount), 0.0) AS PostedCredit
                      FROM ac_head_master h
                      LEFT JOIN daybook_summary d
                             ON d.ledger_id = h.id AND d.entry_date BETWEEN @From AND @To
                      WHERE COALESCE(h.ac_group_id, 0) <> 0
                        AND COALESCE(h.ac_category_id, 0) NOT IN (@OpeningCat, @ClosingCat)
                      GROUP BY h.id, h.ac_head_name, h.ac_group_id
                      ORDER BY h.ac_head_name",
                    new
                    {
                        From = from,
                        To = to,
                        OpeningCat = AccountCategoryRepository.OpeningStockCategoryCode,
                        ClosingCat = AccountCategoryRepository.ClosingStockCategoryCode
                    }).AsList();

                var directExpenses = new List<(string Name, double Amount)>();
                var directIncome = new List<(string Name, double Amount)>();
                var indirectExpenses = new List<(string Name, double Amount)>();
                var indirectIncome = new List<(string Name, double Amount)>();

                foreach (var r in raw)
                {
                    double net = r.PostedCredit - r.PostedDebit;
                    if (System.Math.Abs(net) < 0.005) continue;

                    bool isDirect = r.AcGroupId == 1;
                    if (net > 0) (isDirect ? directIncome : indirectIncome).Add((r.AcHeadName, net));
                    else (isDirect ? directExpenses : indirectExpenses).Add((r.AcHeadName, -net));
                }

                var stockHeads = db.Query<RawStockCategoryRow>(
                    @"SELECT ac_head_name AS AcHeadName, ac_category_id AS AcCategoryId,
                             opening_debit AS OpeningDebit, opening_credit AS OpeningCredit
                      FROM ac_head_master
                      WHERE ac_category_id IN (@OpeningCat, @ClosingCat)
                      ORDER BY ac_head_name",
                    new
                    {
                        OpeningCat = AccountCategoryRepository.OpeningStockCategoryCode,
                        ClosingCat = AccountCategoryRepository.ClosingStockCategoryCode
                    }).AsList();

                var openingStockLines = new List<(string Name, double Amount)>();
                var closingStockLines = new List<(string Name, double Amount)>();

                foreach (var h in stockHeads)
                {
                    double value = GetStockHeadValue(h);
                    if (System.Math.Abs(value) < 0.005) continue;

                    if (h.AcCategoryId == AccountCategoryRepository.OpeningStockCategoryCode)
                        openingStockLines.Add((h.AcHeadName, value));
                    else
                        closingStockLines.Add((h.AcHeadName, value));
                }

                directExpenses.InsertRange(0, openingStockLines);
                directIncome.InsertRange(0, closingStockLines);

                double totalDirectExpense = directExpenses.Sum(x => x.Amount);
                double totalDirectIncome = directIncome.Sum(x => x.Amount);
                double grossProfit = totalDirectIncome - totalDirectExpense;

                var rows = ZipProfitLossRows(directExpenses, directIncome);
                AppendBalancingRow(rows,
                    isExpenseSide: grossProfit >= 0,
                    name: grossProfit >= 0 ? "Gross Profit c/d" : "Gross Loss c/d",
                    amount: System.Math.Abs(grossProfit));
                AppendTotalRow(rows,
                    expenseTotal: totalDirectExpense + (grossProfit >= 0 ? grossProfit : 0),
                    incomeTotal: totalDirectIncome + (grossProfit < 0 ? -grossProfit : 0),
                    isGrossProfitRow: true);

                double totalIndirectExpense = indirectExpenses.Sum(x => x.Amount)
                    + (grossProfit < 0 ? -grossProfit : 0);
                double totalIndirectIncome = indirectIncome.Sum(x => x.Amount)
                    + (grossProfit >= 0 ? grossProfit : 0);
                double netProfit = totalIndirectIncome - totalIndirectExpense;

                rows.AddRange(ZipProfitLossRows(indirectExpenses, indirectIncome));
                AppendBalancingRow(rows,
                    isExpenseSide: grossProfit < 0,
                    name: grossProfit >= 0 ? "Gross Profit b/d" : "Gross Loss b/d",
                    amount: System.Math.Abs(grossProfit));
                AppendBalancingRow(rows,
                    isExpenseSide: netProfit >= 0,
                    name: netProfit >= 0 ? "Net Profit for the period" : "Net Loss for the period",
                    amount: System.Math.Abs(netProfit));
                AppendTotalRow(rows,
                    expenseTotal: totalIndirectExpense + (netProfit >= 0 ? netProfit : 0),
                    incomeTotal: totalIndirectIncome + (netProfit < 0 ? -netProfit : 0),
                    isGrossProfitRow: false);

                return rows;
            }
        }

        private class RawStockCategoryRow
        {
            public string AcHeadName { get; set; }
            public int AcCategoryId { get; set; }
            public double OpeningDebit { get; set; }
            public double OpeningCredit { get; set; }
        }

        private static double GetStockHeadValue(RawStockCategoryRow h)
        {
            return h.AcCategoryId == AccountCategoryRepository.OpeningStockCategoryCode
                ? h.OpeningDebit - h.OpeningCredit
                : h.OpeningCredit - h.OpeningDebit;
        }

        private static List<ProfitLossRow> ZipProfitLossRows(
            List<(string Name, double Amount)> expenses,
            List<(string Name, double Amount)> income)
        {
            var rows = new List<ProfitLossRow>();
            int max = System.Math.Max(expenses.Count, income.Count);

            for (int i = 0; i < max; i++)
            {
                rows.Add(new ProfitLossRow
                {
                    ExpenseParticulars = i < expenses.Count ? expenses[i].Name : null,
                    ExpenseAmount = i < expenses.Count ? expenses[i].Amount : (double?)null,
                    IncomeParticulars = i < income.Count ? income[i].Name : null,
                    IncomeAmount = i < income.Count ? income[i].Amount : (double?)null,
                });
            }

            return rows;
        }

        private static void AppendBalancingRow(
            List<ProfitLossRow> rows, bool isExpenseSide, string name, double amount)
        {
            rows.Add(new ProfitLossRow
            {
                IsBalancingRow = true,
                ExpenseParticulars = isExpenseSide ? name : null,
                ExpenseAmount = isExpenseSide ? amount : (double?)null,
                IncomeParticulars = isExpenseSide ? null : name,
                IncomeAmount = isExpenseSide ? (double?)null : amount,
            });
        }

        private static void AppendTotalRow(
            List<ProfitLossRow> rows, double expenseTotal, double incomeTotal, bool isGrossProfitRow)
        {
            rows.Add(new ProfitLossRow
            {
                IsTotalRow = !isGrossProfitRow,
                IsGrossProfitRow = isGrossProfitRow,
                ExpenseParticulars = "Total",
                ExpenseAmount = expenseTotal,
                IncomeParticulars = "Total",
                IncomeAmount = incomeTotal
            });
        }

        public List<BalanceSheetRow> GetBalanceSheet(DateTime asAt)
        {
            int cashLedgerId = GetOrCreateCashHeadId();
            double cashNet = GetCashInHandBalance(asAt);

            DateTime fyStart = GetFinancialYearStart(asAt);
            double netProfit = GetNetProfitForPeriod(fyStart, asAt);

            using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
            {
                var to = asAt.ToString("yyyy-MM-dd");

                var raw = db.Query<RawTrialBalanceRow>(
                    @"SELECT h.id AS LedgerId,
                             h.ac_head_code AS AcHeadCode,
                             h.ac_head_name AS AcHeadName,
                             h.opening_debit AS OpeningDebit,
                             h.opening_credit AS OpeningCredit,
                             COALESCE(SUM(d.debit_amount), 0.0) AS PostedDebit,
                             COALESCE(SUM(d.credit_amount), 0.0) AS PostedCredit
                      FROM ac_head_master h
                      LEFT JOIN daybook_summary d
                             ON d.ledger_id = h.id AND d.entry_date <= @To
                      WHERE COALESCE(h.ac_group_id, 0) = 0
                        AND COALESCE(h.ac_category_id, 0) NOT IN (@OpeningCat, @ClosingCat)
                      GROUP BY h.id, h.ac_head_code, h.ac_head_name, h.opening_debit, h.opening_credit
                      ORDER BY h.ac_head_name",
                    new
                    {
                        To = to,
                        OpeningCat = AccountCategoryRepository.OpeningStockCategoryCode,
                        ClosingCat = AccountCategoryRepository.ClosingStockCategoryCode
                    }).AsList();

                var assets = new List<(string Name, double Amount)>();
                var liabilities = new List<(string Name, double Amount)>();

                foreach (var r in raw)
                {
                    bool isCashRow = r.LedgerId == cashLedgerId;

                    double net = isCashRow
                        ? cashNet
                        : (r.OpeningDebit - r.OpeningCredit) + r.PostedDebit - r.PostedCredit;

                    if (System.Math.Abs(net) < 0.005) continue;

                    if (net > 0) assets.Add((r.AcHeadName, net));
                    else liabilities.Add((r.AcHeadName, -net));
                }

                var closingStockHeads = db.Query<RawStockCategoryRow>(
                    @"SELECT ac_head_name AS AcHeadName, ac_category_id AS AcCategoryId,
                             opening_debit AS OpeningDebit, opening_credit AS OpeningCredit
                      FROM ac_head_master
                      WHERE ac_category_id = @ClosingCat
                      ORDER BY ac_head_name",
                    new { ClosingCat = AccountCategoryRepository.ClosingStockCategoryCode }).AsList();

                var closingStockAssets = new List<(string Name, double Amount)>();
                foreach (var h in closingStockHeads)
                {
                    double net = GetStockHeadValue(h);
                    if (System.Math.Abs(net) < 0.005) continue;

                    if (net > 0) closingStockAssets.Add((h.AcHeadName, net));
                    else liabilities.Insert(0, (h.AcHeadName, -net));
                }
                assets.InsertRange(0, closingStockAssets);

                if (netProfit >= 0) liabilities.Add(("Profit", netProfit));
                else assets.Add(("Loss", -netProfit));

                var rows = new List<BalanceSheetRow>();
                int max = System.Math.Max(liabilities.Count, assets.Count);

                for (int i = 0; i < max; i++)
                {
                    rows.Add(new BalanceSheetRow
                    {
                        LiabilityParticulars = i < liabilities.Count ? liabilities[i].Name : null,
                        LiabilityAmount = i < liabilities.Count ? liabilities[i].Amount : (double?)null,
                        AssetParticulars = i < assets.Count ? assets[i].Name : null,
                        AssetAmount = i < assets.Count ? assets[i].Amount : (double?)null,
                    });
                }

                rows.Add(new BalanceSheetRow
                {
                    IsTotalRow = true,
                    LiabilityParticulars = "Total",
                    LiabilityAmount = liabilities.Sum(x => x.Amount),
                    AssetParticulars = "Total",
                    AssetAmount = assets.Sum(x => x.Amount)
                });

                return rows;
            }
        }

        private LedgerReportRow BuildLedgerOpeningRow(string entryDate, double balance)
        {
            return new LedgerReportRow
            {
                EntryDate = entryDate,
                TransactionType = "OpeningBalance",
                Narration = "",
                DebitAmount = 0,
                CreditAmount = balance,
                RunningBalance = balance,
                IsOpeningBalance = true,
                SortKey = -1
            };
        }
    }
}