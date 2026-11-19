using System.Collections.Generic;
using System.Data;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // ─────────────────────────────────────────────────────────────────────
    // PAYMENT ENTRY — inventory.db, table payment_entry_details.
    // Same structure as ReceiptRepository minus cash discount. Table moved
    // here (was accounts.db) — see ReceiptRepository.cs for why, and
    // PaymentRepository.Accounts.cs for the posting/resync side.
    // ─────────────────────────────────────────────────────────────────────
    public partial class PaymentRepository
    {
        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute(@"
                    CREATE TABLE IF NOT EXISTS payment_entry_details (
                        payment_no            INTEGER PRIMARY KEY AUTOINCREMENT,
                        payment_date           TEXT NOT NULL,
                        ac_head_code           TEXT NOT NULL,
                        ac_head_name           TEXT,
                        narration              TEXT,
                        payment_amount         REAL DEFAULT 0,
                        payment_mode           TEXT,
                        bank_ac_head_code      TEXT,
                        bank_ac_head_name      TEXT,
                        bank_narration         TEXT,
                        ledger_balance         REAL DEFAULT 0,
                        cancel_status          INTEGER NOT NULL DEFAULT 0,
                        created_by             TEXT,
                        created_date           TEXT,
                        last_modified_by       TEXT,
                        last_modified_date     TEXT
                    );");

                AccountsSyncStatus.EnsureColumn(db, "payment_entry_details");
            }
        }

        public int GetNextPaymentNo()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int maxNo = db.ExecuteScalar<int?>("SELECT MAX(payment_no) FROM payment_entry_details") ?? 0;
                return maxNo + 1;
            }
        }

        public void Save(PaymentEntry entry)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    INSERT INTO payment_entry_details
                        (payment_date, ac_head_code, ac_head_name, narration,
                         payment_amount, payment_mode,
                         bank_ac_head_code, bank_ac_head_name, bank_narration,
                         ledger_balance, cancel_status, created_by, created_date)
                    VALUES
                        (@PaymentDate, @AcHeadCode, @AcHeadName, @Narration,
                         @PaymentAmount, @PaymentMode,
                         @BankAcHeadCode, @BankAcHeadName, @BankNarration,
                         @LedgerBalance, 0, @CreatedBy, @CreatedDate);
                    SELECT last_insert_rowid();";

                entry.PaymentNo = db.ExecuteScalar<int>(sql, entry);
            }

            PostToAccounts(entry, entry.CreatedBy);
        }

        public void Update(PaymentEntry entry)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    UPDATE payment_entry_details SET
                        payment_date         = @PaymentDate,
                        ac_head_code          = @AcHeadCode,
                        ac_head_name          = @AcHeadName,
                        narration             = @Narration,
                        payment_amount        = @PaymentAmount,
                        payment_mode          = @PaymentMode,
                        bank_ac_head_code     = @BankAcHeadCode,
                        bank_ac_head_name     = @BankAcHeadName,
                        bank_narration        = @BankNarration,
                        ledger_balance        = @LedgerBalance,
                        last_modified_by      = @LastModifiedBy,
                        last_modified_date    = @LastModifiedDate
                    WHERE payment_no = @PaymentNo";

                db.Execute(sql, entry);
            }

            PostToAccounts(entry, entry.LastModifiedBy);
        }

        public void CancelPayment(int paymentNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute("UPDATE payment_entry_details SET cancel_status = 1 WHERE payment_no = @No",
                    new { No = paymentNo });
            }

            ReverseInAccounts(paymentNo);
        }

        public PaymentEntry GetByPaymentNo(int paymentNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<PaymentEntry>(
                    @"SELECT payment_no AS PaymentNo, payment_date AS PaymentDate,
                             ac_head_code AS AcHeadCode, ac_head_name AS AcHeadName,
                             narration AS Narration, payment_amount AS PaymentAmount,
                             payment_mode AS PaymentMode,
                             bank_ac_head_code AS BankAcHeadCode, bank_ac_head_name AS BankAcHeadName,
                             bank_narration AS BankNarration, ledger_balance AS LedgerBalance,
                             cancel_status AS CancelStatus
                      FROM payment_entry_details WHERE payment_no = @No",
                    new { No = paymentNo });
            }
        }

        public List<PaymentEntry> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<PaymentEntry>(
                    @"SELECT payment_no AS PaymentNo, payment_date AS PaymentDate,
                             ac_head_code AS AcHeadCode, ac_head_name AS AcHeadName,
                             narration AS Narration, payment_amount AS PaymentAmount,
                             payment_mode AS PaymentMode,
                             bank_ac_head_code AS BankAcHeadCode, bank_ac_head_name AS BankAcHeadName,
                             bank_narration AS BankNarration, ledger_balance AS LedgerBalance,
                             cancel_status AS CancelStatus
                      FROM payment_entry_details ORDER BY payment_no DESC").AsList();
            }
        }
    }
}