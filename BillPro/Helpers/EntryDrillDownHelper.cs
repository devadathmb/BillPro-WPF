using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BillPro.Models;
using BillPro.Repositories;
using BillPro.Views;

namespace BillPro.Helpers
{
    /// <summary>
    /// Shared "double-click a Daybook/Ledger Report row to open the entry
    /// that posted it" behavior — used by both DaybookReportView and
    /// LedgerReportView, since both read off the same daybook_summary rows
    /// and need identical handling.
    ///
    /// Sales and SalesReturn are the only two that ever group (see
    /// FinancialSyncService.IsGroupedRole), so they're the only ones that
    /// can land on the bill-picker popup. Purchase, PurchaseReturn, Journal,
    /// Receipt, and Payment are always granular — one row per voucher — so
    /// a double-click on one of those jumps straight to the entry. Every
    /// VoucherRef here is built as "{Prefix}-{Number}" by the owning
    /// repository's *.Accounts.cs (PurchaseVoucherRef, FormatEntryNo,
    /// ReceiptVoucherRef, PaymentVoucherRef, JournalEntryRepository.
    /// FormattedEntryNo) — see TryParseSuffixNumber.
    ///
    /// NOTE: Receipt Entry / Payment Entry views must call
    /// EntryEditNavigator.Subscribe(EntryChannels.ReceiptEntry / .PaymentEntry, ...)
    /// in their constructor (and Unsubscribe from Dispose) for these two
    /// cases to actually do anything — same wiring PurchaseEntryView etc.
    /// already have, see EntryEditNavigator's class doc comment. Without
    /// that subscription RequestEdit below is just a silent no-op.
    /// </summary>
    public static class EntryDrillDownHelper
    {
        private static readonly SalesRepository _salesRepo = new();
        private static readonly SalesReturnRepository _salesReturnRepo = new();

        /// <summary>
        /// Handles a double-click on one real posting row. Silently does
        /// nothing for transaction types this helper doesn't yet resolve,
        /// or if the underlying bill/entry can no longer be found (e.g.
        /// deleted since the report was loaded).
        /// </summary>
        public static void HandleRowActivated(
            FrameworkElement owner,
            string transactionType,
            DateTime entryDate,
            string voucherRef,
            string groupSeries,
            string groupBillNoFrom,
            string groupBillNoTo)
        {
            bool isGroupedRow = !string.IsNullOrEmpty(groupSeries);

            switch (transactionType)
            {
                case "Sales":
                    if (isGroupedRow)
                        ShowSalesPickerAndNavigate(owner, entryDate, groupSeries);
                    else
                        NavigateToSalesBill(voucherRef);
                    break;

                case "SalesReturn":
                    if (isGroupedRow)
                        ShowSalesReturnPickerAndNavigate(owner, entryDate);
                    else if (TryParseSuffixNumber(voucherRef, out int srEntryNo))
                        OpenSalesReturnEntry(srEntryNo);
                    break;

                case "Purchase":
                    if (TryParseSuffixNumber(voucherRef, out int purchaseNo))
                    {
                        MainWindow.Instance?.OpenOrActivateTitle("Purchase Entry");
                        EntryEditNavigator.RequestEdit(EntryChannels.PurchaseEntry, purchaseNo);
                    }
                    break;

                case "PurchaseReturn":
                    if (TryParseSuffixNumber(voucherRef, out int prEntryNo))
                    {
                        MainWindow.Instance?.OpenOrActivateTitle("Purchase Return Entry");
                        EntryEditNavigator.RequestEdit(EntryChannels.PurchaseReturnEntry, prEntryNo);
                    }
                    break;

                case "Journal":
                    if (TryParseSuffixNumber(voucherRef, out int jvEntryNo))
                    {
                        MainWindow.Instance?.OpenOrActivateTitle("Journal Entry");
                        EntryEditNavigator.RequestEdit(EntryChannels.JournalEntry, jvEntryNo);
                    }
                    break;

                case "Receipt":
                    if (TryParseSuffixNumber(voucherRef, out int receiptNo))
                    {
                        MainWindow.Instance?.OpenOrActivateTitle("Receipt Entry");
                        EntryEditNavigator.RequestEdit(EntryChannels.ReceiptEntry, receiptNo);
                    }
                    break;

                case "Payment":
                    if (TryParseSuffixNumber(voucherRef, out int paymentNo))
                    {
                        MainWindow.Instance?.OpenOrActivateTitle("Payment Entry");
                        EntryEditNavigator.RequestEdit(EntryChannels.PaymentEntry, paymentNo);
                    }
                    break;

                // No other transaction type currently reaches this helper.
                default:
                    break;
            }
        }

        // A VoucherRef here is always "{Prefix}-{Number}" — PUR-10, PR-3,
        // RCPT-7, PAY-4, SR-3, JV-6 — built by each repository's
        // *.Accounts.cs. Only the number after the first '-' is ever
        // meaningful; the prefix is display-only and never re-parsed
        // anywhere else in the app either (see SalesReturnRepository.
        // FormatEntryNo's doc comment), so splitting on the first '-' is
        // safe and stable.
        private static bool TryParseSuffixNumber(string voucherRef, out int number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(voucherRef)) return false;

            int dash = voucherRef.IndexOf('-');
            string numPart = dash >= 0 ? voucherRef.Substring(dash + 1) : voucherRef;

            return int.TryParse(numPart, out number);
        }

        // ────────────────────────────────────────────────────────────────
        // SALES
        // ────────────────────────────────────────────────────────────────
        private static void ShowSalesPickerAndNavigate(FrameworkElement owner, DateTime entryDate, string series)
        {
            List<SalesSummary> bills;
            try
            {
                bills = _salesRepo.GetBillsBySeriesAndDate(series, entryDate);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading bills for this line:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (bills.Count == 0)
            {
                MessageBox.Show("No bills could be found for this line (they may have been deleted).",
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (bills.Count == 1)
            {
                OpenSalesEntry(bills[0].Prefix, bills[0].BillNo);
                return;
            }

            var rows = bills.Select(b => new BillRangePickerWindow.Row
            {
                FormattedNo = b.FormattedBillNo,
                DateLabel = b.BillDate.ToString("dd-MMM-yyyy"),
                PartyName = string.IsNullOrWhiteSpace(b.CustomerName) ? "Walk-in" : b.CustomerName,
                Amount = b.NetTotal,
                Key = (b.Prefix, b.BillNo)
            }).ToList();

            var picker = new BillRangePickerWindow($"Sales Bills — {series}, {entryDate:dd-MMM-yyyy}", rows)
            {
                Owner = Window.GetWindow(owner)
            };

            if (picker.ShowDialog() == true && picker.SelectedKey is ValueTuple<string, int> key)
                OpenSalesEntry(key.Item1, key.Item2);
        }

        /// <summary>
        /// Looks up a Sales bill by its FormattedBillNo and opens/activates
        /// Sales Entry on it — public so callers outside the drill-down
        /// switch (e.g. DashboardView's Recent Bills list, which only has
        /// FormattedBillNo to go on) can reuse the same lookup+navigate
        /// logic instead of duplicating it.
        /// </summary>
        public static void NavigateToSalesBill(string formattedBillNo)
        {
            if (string.IsNullOrWhiteSpace(formattedBillNo)) return;

            var summary = _salesRepo.GetSummaryByFormattedBillNo(formattedBillNo);
            if (summary == null)
            {
                MessageBox.Show("This bill could not be found (it may have been deleted).",
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenSalesEntry(summary.Prefix, summary.BillNo);
        }

        private static void OpenSalesEntry(string prefix, int billNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Sales Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.SalesEntry, (prefix, billNo));
        }

        // ────────────────────────────────────────────────────────────────
        // SALES RETURN
        // ────────────────────────────────────────────────────────────────
        private static void ShowSalesReturnPickerAndNavigate(FrameworkElement owner, DateTime entryDate)
        {
            List<SalesReturnSummary> entries;
            try
            {
                entries = _salesReturnRepo.GetEntriesByDate(entryDate);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading entries for this line:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (entries.Count == 0)
            {
                MessageBox.Show("No entries could be found for this line (they may have been deleted).",
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (entries.Count == 1)
            {
                OpenSalesReturnEntry(entries[0].EntryNo);
                return;
            }

            var rows = entries.Select(en => new BillRangePickerWindow.Row
            {
                FormattedNo = en.FormattedEntryNo,
                DateLabel = entryDate.ToString("dd-MMM-yyyy"),
                PartyName = string.IsNullOrWhiteSpace(en.CustomerName) ? "Walk-in" : en.CustomerName,
                Amount = en.NetTotal,
                Key = en.EntryNo
            }).ToList();

            var picker = new BillRangePickerWindow($"Sales Return Entries — {entryDate:dd-MMM-yyyy}", rows)
            {
                Owner = Window.GetWindow(owner)
            };

            if (picker.ShowDialog() == true && picker.SelectedKey is int entryNo)
                OpenSalesReturnEntry(entryNo);
        }

        private static void OpenSalesReturnEntry(int entryNo)
        {
            MainWindow.Instance?.OpenOrActivateTitle("Sales Return Entry");
            EntryEditNavigator.RequestEdit(EntryChannels.SalesReturnEntry, entryNo);
        }
    }
}