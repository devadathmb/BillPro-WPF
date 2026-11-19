using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
	public partial class JournalEntryRepository
	{
		private readonly FinancialSyncService _financialSync = new FinancialSyncService();
		private readonly TaxLedgerRepository _taxLedgerRepo = new TaxLedgerRepository();

		private void PostToAccounts(JournalEntrySummary header, List<JournalEntryLine> lines, string currentUser)
		{
			var voucher = BuildVoucher(header, lines);

			try
			{
				_financialSync.Post(voucher, currentUser);
				using (IDbConnection db = DatabaseConfig.GetConnection())
					AccountsSyncStatus.MarkSynced(db, "journal_entry_summary", "entry_no=@E", new { E = header.EntryNo });
			}
			catch
			{
				using (IDbConnection db = DatabaseConfig.GetConnection())
					AccountsSyncStatus.MarkPending(db, "journal_entry_summary", "entry_no=@E", new { E = header.EntryNo });
			}
		}

		private void ReverseInAccounts(int entryNo, string entryDate)
		{
			try
			{
				_financialSync.Reverse(TransactionType.Journal, FormattedEntryNo(entryNo), entryDate);
				using (IDbConnection db = DatabaseConfig.GetConnection())
					AccountsSyncStatus.MarkSynced(db, "journal_entry_summary", "entry_no=@E", new { E = entryNo });
			}
			catch
			{
				using (IDbConnection db = DatabaseConfig.GetConnection())
					AccountsSyncStatus.MarkPending(db, "journal_entry_summary", "entry_no=@E", new { E = entryNo });
			}
		}

		/// <summary>
		/// A journal voucher is pure double-entry — every line is its own
		/// settlement leg (Debit or Credit against whichever a/c head was
		/// picked), no tax-percent lines at all (Lines stays empty, so
		/// FinancialSyncService's tax-grouping/Discount/RoundOff paths are
		/// simply no-ops for TransactionType.Journal).
		///
		/// A line that happens to hit the canonical Cash head is tagged
		/// SettlementLegType.Cash so it posts into cash_transactions (the
		/// Cash Book) instead of daybook_summary — the same routing every
		/// other module already gives a real cash movement. Otherwise a
		/// journal adjustment to cash would silently go untracked in the
		/// Cash Book, and would only ever surface on the Ledger Report for
		/// the Cash head — which never shows Cash-tagged legs in the first
		/// place, see FinancialSyncService.PostLeg.
		/// </summary>
		private FinancialVoucher BuildVoucher(JournalEntrySummary header, List<JournalEntryLine> lines)
		{
			int cashHeadId = _taxLedgerRepo.GetOrCreateCashHeadId();
			var legs = new List<SettlementLeg>();

			foreach (var line in lines)
			{
				int ledgerId = GetAcHeadIdByCode(line.AcHeadCode);
				if (ledgerId == 0) continue; // shouldn't happen — head is always picked from a valid combo

				legs.Add(new SettlementLeg
				{
					LedgerId = ledgerId,
					DebitAmount = line.Mode == "Debit" ? line.Amount : 0,
					CreditAmount = line.Mode == "Credit" ? line.Amount : 0,
					LegType = ledgerId == cashHeadId ? SettlementLegType.Cash : SettlementLegType.Party
				});
			}

			return new FinancialVoucher
			{
				Type = TransactionType.Journal,
				VoucherRef = FormattedEntryNo(header.EntryNo),
				EntryDate = header.EntryDate,
				Lines = new List<FinancialVoucherLine>(),
				SettlementLegs = legs
			};
		}

		private int GetAcHeadIdByCode(string acHeadCode)
		{
			using (IDbConnection db = DatabaseConfig.GetAccountsConnection())
				return db.QueryFirstOrDefault<int?>(
					"SELECT id FROM ac_head_master WHERE ac_head_code = @Code", new { Code = acHeadCode }) ?? 0;
		}

		/// <summary>
		/// Retries accounts posting for every journal voucher flagged
		/// accounts_synced = 0, optionally scoped to an entry-date range —
		/// same convention as every other module's ResyncPending (see
		/// PurchaseReturnRepository.Accounts.cs).
		/// </summary>
		public ResyncOutcome ResyncPending(string currentUser, DateTime? fromDate = null, DateTime? toDate = null, bool forceAll = false)
		{
			var outcome = new ResyncOutcome();
			List<int> pending;

			using (IDbConnection db = DatabaseConfig.GetConnection())
			{
				string sql = forceAll
					? "SELECT entry_no FROM journal_entry_summary WHERE 1=1"
					: "SELECT entry_no FROM journal_entry_summary WHERE accounts_synced = 0";
				if (fromDate.HasValue) sql += " AND entry_date >= @From";
				if (toDate.HasValue) sql += " AND entry_date <= @To";

				pending = db.Query<int>(sql,
					new { From = fromDate?.ToString("yyyy-MM-dd"), To = toDate?.ToString("yyyy-MM-dd") }).AsList();
			}
			outcome.Pending = pending.Count;

			foreach (var entryNo in pending)
			{
				var header = GetByEntryNo(entryNo);
				if (header == null) continue;
				var lines = GetLines(entryNo);

				try
				{
					if (header.CancelStatus == 1)
						_financialSync.Reverse(TransactionType.Journal, FormattedEntryNo(entryNo), header.EntryDate);
					else
						_financialSync.Post(BuildVoucher(header, lines), currentUser);

					using (IDbConnection db = DatabaseConfig.GetConnection())
						AccountsSyncStatus.MarkSynced(db, "journal_entry_summary", "entry_no=@E", new { E = entryNo });
					outcome.Succeeded++;
				}
				catch (Exception ex)
				{
					outcome.FailedRefs.Add($"{FormattedEntryNo(entryNo)} — {ex.Message}");
				}
			}

			return outcome;
		}
	}
}