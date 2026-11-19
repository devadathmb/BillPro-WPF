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
    // SALES SERIES CONFIGURATION
    // One row per registered prefix (e.g. "GA" for B2C-Local cash sales,
    // "GB" for B2B-Interstate credit sales, etc). The Sales Entry combo picks
    // one of these; the bill number is Prefix + running number (from
    // StartNumber, tracked via CurrentNumber) + an optional suffix.
    // ═══════════════════════════════════════════════════════════════════════
    public class SalesSeriesRepository
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new();

        public void InitializeDatabase()
        {
            lock (_initLock)
            {
                if (_initialized) return;

                using (IDbConnection db = DatabaseConfig.GetConnection())
                {
                    db.Execute(@"
                        CREATE TABLE IF NOT EXISTS sales_series_master (
                            prefix            TEXT PRIMARY KEY,
                            start_number      INTEGER NOT NULL DEFAULT 1,
                            current_number    INTEGER NOT NULL DEFAULT 0,
                            customer_type     TEXT NOT NULL,      -- B2B / B2C
                            transaction_type  TEXT NOT NULL,      -- Local / Interstate / Export
                            suffix_type       TEXT NOT NULL DEFAULT 'None',   -- None / FinancialYear / Custom
                            suffix_value      TEXT,
                            is_active         INTEGER NOT NULL DEFAULT 1,
                            is_default        INTEGER NOT NULL DEFAULT 0     -- exactly one row may have this set to 1
                        );");

                    // Lightweight migration for databases created before is_default
                    // existed. SQLite has no "ADD COLUMN IF NOT EXISTS", so probe
                    // the schema and only alter it if the column is missing.
                    bool hasIsDefault = db.Query("PRAGMA table_info(sales_series_master)")
                        .Any(col => string.Equals((string)col.name, "is_default", StringComparison.OrdinalIgnoreCase));

                    if (!hasIsDefault)
                    {
                        db.Execute("ALTER TABLE sales_series_master ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;");
                    }
                }

                _initialized = true;
            }
        }

        public List<SalesSeriesMaster> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<SalesSeriesMaster>(@"
                    SELECT prefix            AS Prefix,
                           start_number      AS StartNumber,
                           current_number    AS CurrentNumber,
                           customer_type     AS CustomerType,
                           transaction_type  AS TransactionType,
                           suffix_type       AS SuffixType,
                           suffix_value      AS SuffixValue,
                           is_active         AS IsActive,
                           is_default        AS IsDefault
                    FROM sales_series_master
                    ORDER BY prefix").ToList();
            }
        }

        public List<SalesSeriesMaster> GetActive()
        {
            return GetAll().Where(s => s.IsActive == 1).ToList();
        }

        /// <summary>
        /// Returns the series flagged as default among the active ones, if
        /// any. Sales Entry uses this to pre-select the series combo on a
        /// new bill instead of always defaulting to the first row.
        /// </summary>
        public SalesSeriesMaster GetDefault()
        {
            return GetActive().FirstOrDefault(s => s.IsDefault == 1);
        }

        /// <summary>
        /// Marks the given prefix as THE default series and clears the flag
        /// on every other row, atomically, so the "default" radio behaves as
        /// a true single-select across the whole table (not per category).
        /// Passing null/empty clears the default entirely.
        /// </summary>
        public void SetDefault(string prefix)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute("UPDATE sales_series_master SET is_default = 0");

                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    db.Execute("UPDATE sales_series_master SET is_default = 1 WHERE prefix = @Prefix",
                        new { Prefix = prefix });
                }
            }
        }

        public bool PrefixExists(string prefix)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM sales_series_master WHERE prefix = @Prefix",
                    new { Prefix = prefix }) > 0;
            }
        }

        public void AddSeries(SalesSeriesMaster series)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute(@"
                    INSERT INTO sales_series_master
                        (prefix, start_number, current_number, customer_type,
                         transaction_type, suffix_type, suffix_value, is_active)
                    VALUES
                        (@Prefix, @StartNumber, @InitialCurrentNumber, @CustomerType,
                         @TransactionType, @SuffixType, @SuffixValue, @IsActive)",
                    new
                    {
                        series.Prefix,
                        series.StartNumber,
                        // One less than StartNumber, not StartNumber itself — the next
                        // claim always does CurrentNumber + 1, so this makes the FIRST
                        // bill actually land on StartNumber instead of skipping it.
                        InitialCurrentNumber = series.StartNumber - 1,
                        series.CustomerType,
                        series.TransactionType,
                        series.SuffixType,
                        series.SuffixValue,
                        series.IsActive
                    });
            }
        }

        public void UpdateSeries(SalesSeriesMaster series)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                // start_number and current_number are intentionally NOT updated
                // here — changing the starting point after bills have already
                // been issued from a series would desync FormattedBillNo
                // uniqueness. Only the descriptive/config fields are editable.
                db.Execute(@"
                    UPDATE sales_series_master
                    SET customer_type    = @CustomerType,
                        transaction_type = @TransactionType,
                        suffix_type      = @SuffixType,
                        suffix_value     = @SuffixValue,
                        is_active        = @IsActive
                    WHERE prefix = @Prefix",
                    series);
            }
        }

        public bool IsSeriesInUse(string prefix)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM sales_summary WHERE prefix = @Prefix",
                    new { Prefix = prefix }) > 0;
            }
        }

        public void DeleteSeries(string prefix)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute("DELETE FROM sales_series_master WHERE prefix = @Prefix", new { Prefix = prefix });
            }
        }

        /// <summary>
        /// Builds the next bill number for a series WITHOUT consuming it —
        /// used to preview on the Sales Entry screen before Save. The actual
        /// increment happens atomically inside SalesRepository.Save, inside
        /// the same transaction as the rest of the bill, so two bills saved
        /// in quick succession can never collide.
        /// </summary>
        public (int BillNo, string FormattedBillNo) PreviewNextBillNo(string prefix)
        {
            var series = GetAll().FirstOrDefault(s => s.Prefix == prefix);
            if (series == null) throw new InvalidOperationException($"Series \"{prefix}\" not found.");

            int nextBillNo = series.CurrentNumber + 1;
            string formatted = BuildFormattedBillNo(series, nextBillNo);
            return (nextBillNo, formatted);
        }

        /// <summary>
        /// Atomically claims the next bill number for a series inside an
        /// existing transaction. Must be called from within SalesRepository's
        /// Save transaction so the increment and the bill insert commit
        /// together or not at all.
        /// </summary>
        public (int BillNo, string FormattedBillNo) ClaimNextBillNo(IDbConnection db, IDbTransaction tx, string prefix)
        {
            var series = db.QueryFirstOrDefault<SalesSeriesMaster>(@"
                SELECT prefix AS Prefix, start_number AS StartNumber, current_number AS CurrentNumber,
                       customer_type AS CustomerType, transaction_type AS TransactionType,
                       suffix_type AS SuffixType, suffix_value AS SuffixValue, is_active AS IsActive
                FROM sales_series_master WHERE prefix = @Prefix",
                new { Prefix = prefix }, tx);

            if (series == null) throw new InvalidOperationException($"Series \"{prefix}\" not found.");

            int nextBillNo = series.CurrentNumber + 1;

            db.Execute("UPDATE sales_series_master SET current_number = @Next WHERE prefix = @Prefix",
                new { Next = nextBillNo, Prefix = prefix }, tx);

            string formatted = BuildFormattedBillNo(series, nextBillNo);
            return (nextBillNo, formatted);
        }

        private static string BuildFormattedBillNo(SalesSeriesMaster series, int billNo)
        {
            string suffix = series.SuffixType switch
            {
                "FinancialYear" => "-" + GetFinancialYearLabel(series.SuffixValue),
                "Custom" => "-" + (series.SuffixValue ?? ""),
                _ => ""
            };

            return $"{series.Prefix}-{billNo}{suffix}";
        }

        /// <summary>
        /// PLACEHOLDER — Financial Year isn't implemented yet (per project plan,
        /// to be added later). suffixValue currently holds the FY *format*
        /// choice (e.g. "YYYY-YY", "YY-YY") but since there's no FY start-date
        /// config yet, this falls back to the calendar year so the feature is
        /// at least functional and testable end-to-end. Swap this method's
        /// body for a real financial-year lookup once that module exists —
        /// nothing else needs to change, since BuildFormattedBillNo only calls
        /// through this one function.
        /// </summary>
        private static string GetFinancialYearLabel(string format)
        {
            int year = DateTime.Now.Year;
            int nextYear = year + 1;

            return format switch
            {
                "YY-YY" => $"{year % 100:D2}-{nextYear % 100:D2}",
                "YYYY-YYYY" => $"{year}-{nextYear}",
                "YYYY-YY" => $"{year}-{nextYear % 100:D2}",
                _ => $"{year}-{nextYear % 100:D2}"
            };
        }
    }
}