using System.Collections.Generic;
using System.Data;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // Journal Entry's own table lives in the main/default db, same as every
    // other business-document module (Sales, Purchase, Returns, Receipt,
    // Payment) — only ac_head_master and its accounts.db-native neighbours
    // live in accounts.db. See ReceiptRepository.Accounts.cs.Accounts.cs
    // for the identical split this mirrors.
    public partial class JournalEntryRepository
    {
        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute(@"CREATE TABLE IF NOT EXISTS journal_entry_summary (
                                entry_no            INTEGER PRIMARY KEY AUTOINCREMENT,
                                entry_date          TEXT NOT NULL,
                                total_amount        REAL NOT NULL DEFAULT 0,
                                cancel_status       INTEGER NOT NULL DEFAULT 0,
                                accounts_synced     INTEGER NOT NULL DEFAULT 0,
                                created_by          TEXT,
                                created_date        TEXT,
                                last_modified_by    TEXT,
                                last_modified_date  TEXT)");

                db.Execute(@"CREATE TABLE IF NOT EXISTS journal_entry_details (
                                entry_no      INTEGER NOT NULL,
                                sl_no         INTEGER NOT NULL,
                                mode          TEXT NOT NULL,
                                ac_head_code  TEXT NOT NULL,
                                ac_head_name  TEXT,
                                narration     TEXT,
                                amount        REAL NOT NULL DEFAULT 0,
                                PRIMARY KEY (entry_no, sl_no))");
            }
        }

        public int GetNextEntryNo()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int maxNo = db.ExecuteScalar<int?>("SELECT MAX(entry_no) FROM journal_entry_summary") ?? 0;
                return maxNo + 1;
            }
        }

        public string FormattedEntryNo(int entryNo) => $"JV-{entryNo}";

        /// <summary>
        /// Saves a new journal voucher — header + every line, in one
        /// transaction against the main db — then posts it to accounts.
        /// Same "own-table insert, then decoupled accounts post behind a
        /// pending/synced flag" shape as ReceiptRepository.Save.
        /// </summary>
        public void Save(JournalEntrySummary header, List<JournalEntryLine> lines, string currentUser)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    long entryNo = db.ExecuteScalar<long>(
                        @"INSERT INTO journal_entry_summary (
                              entry_date, total_amount, cancel_status, created_by, created_date)
                          VALUES (@EntryDate, @TotalAmount, 0, @CreatedBy, @CreatedDate);
                          SELECT last_insert_rowid();", header, tx);

                    header.EntryNo = (int)entryNo;
                    InsertLines(db, tx, header.EntryNo, lines);

                    tx.Commit();
                }
            }

            PostToAccounts(header, lines, currentUser);
        }

        /// <summary>
        /// Updates an existing journal voucher's header and replaces every
        /// line (delete-then-reinsert, same convention as the return
        /// entries' item-detail tables), then re-posts to accounts —
        /// FinancialSyncService.Post already reverses the voucher's prior
        /// postings before writing the new ones, so this is just "call Post
        /// again" like everywhere else.
        /// </summary>
        public void Update(JournalEntrySummary header, List<JournalEntryLine> lines, string currentUser)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Open();
                using (IDbTransaction tx = db.BeginTransaction())
                {
                    db.Execute(@"UPDATE journal_entry_summary SET
                                    entry_date = @EntryDate,
                                    total_amount = @TotalAmount,
                                    last_modified_by = @LastModifiedBy,
                                    last_modified_date = @LastModifiedDate
                                 WHERE entry_no = @EntryNo", header, tx);

                    db.Execute("DELETE FROM journal_entry_details WHERE entry_no = @EntryNo",
                        new { header.EntryNo }, tx);
                    InsertLines(db, tx, header.EntryNo, lines);

                    tx.Commit();
                }
            }

            PostToAccounts(header, lines, currentUser);
        }

        private void InsertLines(IDbConnection db, IDbTransaction tx, int entryNo, List<JournalEntryLine> lines)
        {
            int slNo = 1;
            foreach (var line in lines)
            {
                line.EntryNo = entryNo;
                line.SlNo = slNo++;

                db.Execute(@"INSERT INTO journal_entry_details (
                                entry_no, sl_no, mode, ac_head_code, ac_head_name, narration, amount)
                             VALUES (@EntryNo, @SlNo, @Mode, @AcHeadCode, @AcHeadName, @Narration, @Amount)",
                    line, tx);
            }
        }

        public JournalEntrySummary GetByEntryNo(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
                return db.QueryFirstOrDefault<JournalEntrySummary>(
                    "SELECT * FROM journal_entry_summary WHERE entry_no = @EntryNo", new { EntryNo = entryNo });
        }

        public List<JournalEntryLine> GetLines(int entryNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
                return db.Query<JournalEntryLine>(
                    "SELECT * FROM journal_entry_details WHERE entry_no = @EntryNo ORDER BY sl_no",
                    new { EntryNo = entryNo }).AsList();
        }

        /// <summary>Header-only list, for the Display search overlay.</summary>
        public List<JournalEntrySummary> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
                return db.Query<JournalEntrySummary>(
                    "SELECT * FROM journal_entry_summary ORDER BY entry_no DESC").AsList();
        }

        public void CancelEntry(int entryNo)
        {
            var header = GetByEntryNo(entryNo);
            if (header == null) return;

            using (IDbConnection db = DatabaseConfig.GetConnection())
                db.Execute("UPDATE journal_entry_summary SET cancel_status = 1 WHERE entry_no = @EntryNo",
                    new { EntryNo = entryNo });

            ReverseInAccounts(entryNo, header.EntryDate);
        }

        /// <summary>
        /// Distinct narrations ever typed on a journal line, most recently
        /// used first — feeds the free-typed-but-suggested Narration combo
        /// on the entry screen. Grows purely from what's actually been
        /// typed; no seed list.
        /// </summary>
        public List<string> GetDistinctNarrations()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
                return db.Query<string>(
                    @"SELECT narration FROM journal_entry_details
                      WHERE narration IS NOT NULL AND narration <> ''
                      GROUP BY narration
                      ORDER BY MAX(entry_no) DESC").AsList();
        }
    }
}