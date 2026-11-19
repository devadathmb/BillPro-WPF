using System.Collections.Generic;
using System.Data;
using Dapper;
using BillPro.DataAccess;

namespace BillPro.Repositories
{
    /// <summary>
    /// Shared helper for the "accounts_synced" flag that gets added (via the
    /// same AddColumnIfMissing self-heal pattern used elsewhere) to
    /// sales_summary / purchase_summary / sales_return_summary /
    /// purchase_return_summary. Lets each repo mark a bill as pending when
    /// FinancialSyncService.Post/Reverse throws (accounts.db unreachable,
    /// locked, etc.) without failing the inventory.db save that already
    /// committed — and gives you one place to sweep and retry them.
    /// </summary>
    public static class AccountsSyncStatus
    {
        public static void EnsureColumn(IDbConnection inventoryDb, string tableName)
        {
            try
            {
                inventoryDb.Execute($"ALTER TABLE {tableName} ADD COLUMN accounts_synced INTEGER NOT NULL DEFAULT 1;");
            }
            catch { /* column already exists */ }
        }

        public static void MarkPending(IDbConnection inventoryDb, string tableName, string keyWhere, object keyParams)
        {
            inventoryDb.Execute($"UPDATE {tableName} SET accounts_synced = 0 WHERE {keyWhere}", keyParams);
        }

        public static void MarkSynced(IDbConnection inventoryDb, string tableName, string keyWhere, object keyParams)
        {
            inventoryDb.Execute($"UPDATE {tableName} SET accounts_synced = 1 WHERE {keyWhere}", keyParams);
        }

        /// <summary>Voucher refs still waiting on an accounts.db post/reverse.</summary>
        public static List<string> GetPendingRefs(IDbConnection inventoryDb, string tableName, string refColumn)
        {
            return new List<string>(inventoryDb.Query<string>(
                $"SELECT {refColumn} FROM {tableName} WHERE accounts_synced = 0"));
        }
    }
}