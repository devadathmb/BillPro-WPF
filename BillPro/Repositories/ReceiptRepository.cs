using System.Collections.Generic;
using System.Data;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // ─────────────────────────────────────────────────────────────────────
    // RECEIPT ENTRY — inventory.db, table receipt_entry_details.
    // Port of the VB6 Customer Receipt Entry form.
    //
    // Table moved here (was accounts.db) so it lives alongside every other
    // entry-form table and follows the same accounts_synced / PostToAccounts
    // / ResyncPending pattern as Sales/Purchase/SalesReturn/PurchaseReturn
    // — see ReceiptRepository.Accounts.cs. The ac_head_code it posts against
    // still resolves directly against accounts.db's ac_head_master with no
    // vendor_customer_master two-hop (see TaxLedgerModels.cs's
    // TransactionType.Receipt comment) — only the row storage moved,
    // not the posting shape.
    // ─────────────────────────────────────────────────────────────────────
    public partial class ReceiptRepository
    {
        public void InitializeDatabase()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute(@"
                    CREATE TABLE IF NOT EXISTS receipt_entry_details (
                        receipt_no            INTEGER PRIMARY KEY AUTOINCREMENT,
                        receipt_date           TEXT NOT NULL,
                        ac_head_code           TEXT NOT NULL,
                        ac_head_name           TEXT,
                        narration              TEXT,
                        receipt_amount         REAL DEFAULT 0,
                        cash_discount          REAL DEFAULT 0,
                        receipt_mode           TEXT,
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

                AccountsSyncStatus.EnsureColumn(db, "receipt_entry_details");
            }
        }

        public int GetNextReceiptNo()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                int maxNo = db.ExecuteScalar<int?>("SELECT MAX(receipt_no) FROM receipt_entry_details") ?? 0;
                return maxNo + 1;
            }
        }

        public void Save(ReceiptEntry entry)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    INSERT INTO receipt_entry_details
                        (receipt_date, ac_head_code, ac_head_name, narration,
                         receipt_amount, cash_discount, receipt_mode,
                         bank_ac_head_code, bank_ac_head_name, bank_narration,
                         ledger_balance, cancel_status, created_by, created_date)
                    VALUES
                        (@ReceiptDate, @AcHeadCode, @AcHeadName, @Narration,
                         @ReceiptAmount, @CashDiscount, @ReceiptMode,
                         @BankAcHeadCode, @BankAcHeadName, @BankNarration,
                         @LedgerBalance, 0, @CreatedBy, @CreatedDate);
                    SELECT last_insert_rowid();";

                entry.ReceiptNo = db.ExecuteScalar<int>(sql, entry);
            }

            PostToAccounts(entry, entry.CreatedBy);
        }

        public void Update(ReceiptEntry entry)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                string sql = @"
                    UPDATE receipt_entry_details SET
                        receipt_date        = @ReceiptDate,
                        ac_head_code         = @AcHeadCode,
                        ac_head_name         = @AcHeadName,
                        narration            = @Narration,
                        receipt_amount       = @ReceiptAmount,
                        cash_discount        = @CashDiscount,
                        receipt_mode         = @ReceiptMode,
                        bank_ac_head_code    = @BankAcHeadCode,
                        bank_ac_head_name    = @BankAcHeadName,
                        bank_narration       = @BankNarration,
                        ledger_balance       = @LedgerBalance,
                        last_modified_by     = @LastModifiedBy,
                        last_modified_date   = @LastModifiedDate
                    WHERE receipt_no = @ReceiptNo";

                db.Execute(sql, entry);
            }

            PostToAccounts(entry, entry.LastModifiedBy);
        }

        public void CancelReceipt(int receiptNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                db.Execute("UPDATE receipt_entry_details SET cancel_status = 1 WHERE receipt_no = @No",
                    new { No = receiptNo });
            }

            ReverseInAccounts(receiptNo);
        }

        public ReceiptEntry GetByReceiptNo(int receiptNo)
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.QueryFirstOrDefault<ReceiptEntry>(
                    @"SELECT receipt_no AS ReceiptNo, receipt_date AS ReceiptDate,
                             ac_head_code AS AcHeadCode, ac_head_name AS AcHeadName,
                             narration AS Narration, receipt_amount AS ReceiptAmount,
                             cash_discount AS CashDiscount, receipt_mode AS ReceiptMode,
                             bank_ac_head_code AS BankAcHeadCode, bank_ac_head_name AS BankAcHeadName,
                             bank_narration AS BankNarration, ledger_balance AS LedgerBalance,
                             cancel_status AS CancelStatus
                      FROM receipt_entry_details WHERE receipt_no = @No",
                    new { No = receiptNo });
            }
        }

        public List<ReceiptEntry> GetAll()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                return db.Query<ReceiptEntry>(
                    @"SELECT receipt_no AS ReceiptNo, receipt_date AS ReceiptDate,
                             ac_head_code AS AcHeadCode, ac_head_name AS AcHeadName,
                             narration AS Narration, receipt_amount AS ReceiptAmount,
                             cash_discount AS CashDiscount, receipt_mode AS ReceiptMode,
                             bank_ac_head_code AS BankAcHeadCode, bank_ac_head_name AS BankAcHeadName,
                             bank_narration AS BankNarration, ledger_balance AS LedgerBalance,
                             cancel_status AS CancelStatus
                      FROM receipt_entry_details ORDER BY receipt_no DESC").AsList();
            }
        }
    }
}