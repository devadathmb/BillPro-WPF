using System;
using System.Collections.Generic;

namespace BillPro.Views
{
    /// <summary>
    /// Generic static bridge between a report (right-click "Edit This Bill" /
    /// "Edit This Entry") and whichever entry view is currently open for that
    /// record type — Purchase Entry, Sales Entry, Sales Return Entry,
    /// Purchase Return Entry, Opening Stock Entry, Account Head Registration,
    /// etc. Reports don't hold a reference to the entry view (it may not
    /// even be open yet), so they just raise a "channel" with a record key;
    /// any currently-subscribed entry view on that channel loads the record
    /// for editing.
    ///
    /// KEY TYPE IS GENERIC — <typeparamref name="TKey"/> is whatever
    /// uniquely identifies a record in that module:
    ///   - Purchase Entry:       int              (PurchaseNo)
    ///   - Sales Entry:          (string Prefix, int BillNo)   — a sales bill
    ///     is only unique by series prefix + running number together (see
    ///     SalesSummary/SalesRepository), so a plain int can't identify one.
    ///     A ValueTuple works out of the box here: it already has correct
    ///     structural equality, so no extra key type needs to be written.
    ///   - Sales Return Entry:    int (EntryNo) — no series/prefix system,
    ///     a single global autoincrement is already unique on its own.
    ///   - Purchase Return Entry: int (EntryNo) — same as above, vendor-side.
    ///   - Journal Entry:         int (EntryNo) — same as above, no series
    ///     concept at all for a journal voucher.
    ///   - Opening Stock Entry:   string (EntryNo) — opening_stock_master.
    ///     entry_no is a TEXT primary key (currently just a stringified
    ///     autoincrement, e.g. "1", "2", ...), never parsed/used as an int
    ///     anywhere in OpeningStockRepository, so the channel is typed to
    ///     match rather than assuming int like the other entry modules.
    ///   - Excess Stock Entry / Short Stock Entry: int (EntryNo) — one
    ///     screen (ExcessShortStockEntryView) handles both directions, so it
    ///     subscribes to BOTH channels; which one a report fires depends on
    ///     which table the row came from (excess_stock_summary vs
    ///     short_stock_summary — see ExcessShortStockRepository).
    ///   NOTE: OpeningStockReportView (the batch-wise report) does NOT raise
    ///   this channel — its rows are GROUP BY batch_id aggregates that can
    ///   represent more than one opening-stock entry each (see EntryCount on
    ///   OpeningStockReportRow), so there is no single EntryNo per row to
    ///   navigate to. If a future one-row-per-entry Opening Stock report is
    ///   added, it can call RequestEdit(EntryChannels.OpeningStockEntry, ...)
    ///   the same way the return reports do.
    /// Each module just calls Subscribe/Unsubscribe/RequestEdit with its own
    /// TKey — nothing in this class needs to change to support a new shape
    /// of key.
    ///
    /// SUBSCRIPTION LIFETIME — important:
    /// Subscribe once, in the entry view's constructor. Do NOT unsubscribe
    /// from UserControl.Unloaded — in AvalonDock (and most docking/tab
    /// hosts), a background/inactive tab's content is detached from the
    /// visual tree (firing Unloaded) even though the tab itself is still
    /// open, and reattached (firing Loaded) when the user switches back.
    /// Unsubscribing on Unloaded silently kills the bridge for any tab that
    /// isn't the currently active one — exactly the "edit from report does
    /// nothing when the entry tab is already open in the background" bug.
    /// Instead, have the entry view implement IDisposable and unsubscribe
    /// there; MainWindow.OpenOrActivate calls Dispose() when the tab is
    /// actually closed (see LayoutDocument.Closed), which is the only point
    /// subscription should end.
    ///
    /// Usage — entry view with a simple int key (Purchase Entry, both
    /// Return entry views, and Journal Entry):
    ///   public PurchaseEntryView() : ... {
    ///       EntryEditNavigator.Subscribe<int>(EntryChannels.PurchaseEntry, OnEditRequested);
    ///   }
    ///   public void Dispose() =>
    ///       EntryEditNavigator.Unsubscribe<int>(EntryChannels.PurchaseEntry, OnEditRequested);
    ///   private void OnEditRequested(int purchaseNo) => LoadBillForEdit(purchaseNo);
    ///
    /// Usage — entry view with a composite key (Sales Entry):
    ///   public SalesEntryView() : ... {
    ///       EntryEditNavigator.Subscribe<(string Prefix, int BillNo)>(EntryChannels.SalesEntry, OnEditRequested);
    ///   }
    ///   public void Dispose() =>
    ///       EntryEditNavigator.Unsubscribe<(string Prefix, int BillNo)>(EntryChannels.SalesEntry, OnEditRequested);
    ///   private void OnEditRequested((string Prefix, int BillNo) key) => LoadBillForEdit(key.Prefix, key.BillNo);
    ///
    /// Usage — entry view with a string key (Opening Stock Entry):
    ///   public OpeningStockEntryView() : ... {
    ///       EntryEditNavigator.Subscribe<string>(EntryChannels.OpeningStockEntry, OnEditRequested);
    ///   }
    ///   public void Dispose() =>
    ///       EntryEditNavigator.Unsubscribe<string>(EntryChannels.OpeningStockEntry, OnEditRequested);
    ///   private void OnEditRequested(string entryNo) => LoadEntryForEdit(entryNo);
    ///
    ///   Usage — report-to-report jump with a composite optional key (Current
    ///   Stock Report → Individual Stock Report, not an entry-edit but the same
    ///   bridge):
    ///     public IndividualStockReportView() : ... {
    ///         EntryEditNavigator.Subscribe<(string ItemCode, int? BatchId)>(EntryChannels.IndividualStockReport, OnShowItemRequested);
    ///     }
    ///     public void Dispose() =>
    ///         EntryEditNavigator.Unsubscribe<(string ItemCode, int? BatchId)>(EntryChannels.IndividualStockReport, OnShowItemRequested);
    ///     private void OnShowItemRequested((string ItemCode, int? BatchId) request) => ...
    ///
    /// Usage — report's right-click handler (any shape):
    ///   MainWindow.Instance?.OpenOrActivateTitle("Purchase Entry");
    ///   EntryEditNavigator.RequestEdit(EntryChannels.PurchaseEntry, row.PurchaseNo);
    ///
    ///   MainWindow.Instance?.OpenOrActivateTitle("Sales Entry");
    ///   EntryEditNavigator.RequestEdit(EntryChannels.SalesEntry, (row.Prefix, row.BillNo));
    ///
    ///   MainWindow.Instance?.OpenOrActivateTitle("Sales Return Entry");
    ///   EntryEditNavigator.RequestEdit(EntryChannels.SalesReturnEntry, row.EntryNo);
    ///
    ///   MainWindow.Instance?.OpenOrActivateTitle("Journal Entry");
    ///   EntryEditNavigator.RequestEdit(EntryChannels.JournalEntry, row.EntryNo);
    ///
    ///   MainWindow.Instance?.OpenOrActivateTitle("Opening Stock Entry");
    ///   EntryEditNavigator.RequestEdit(EntryChannels.OpeningStockEntry, row.EntryNo);
    ///
    ///   MainWindow.Instance?.OpenOrActivateTitle("Individual Stock Report");
    ///   EntryEditNavigator.RequestEdit(EntryChannels.IndividualStockReport, (row.ItemCode, (int?)row.BatchId));
    /// </summary>
    public static class EntryEditNavigator
    {
        // One dictionary shared across every key shape: TKey is erased to
        // plain Delegate for storage, and recovered via an `is Action<TKey>`
        // check in RequestEdit — Subscribe/Unsubscribe/RequestEdit for a
        // given channel must all be called with the same TKey (naturally
        // true in practice, since a channel belongs to exactly one module).
        private static readonly Dictionary<string, Delegate> _channels = new();

        public static void Subscribe<TKey>(string channel, Action<TKey> handler)
        {
            _channels.TryGetValue(channel, out var existing);
            _channels[channel] = existing == null ? handler : Delegate.Combine(existing, handler);
        }

        public static void Unsubscribe<TKey>(string channel, Action<TKey> handler)
        {
            if (!_channels.TryGetValue(channel, out var existing)) return;

            var result = Delegate.Remove(existing, handler);
            if (result == null)
                _channels.Remove(channel);
            else
                _channels[channel] = result;
        }

        /// <summary>Raises the edit request for a channel. No-op if nothing
        /// is currently subscribed (e.g. the entry tab genuinely isn't open
        /// and the caller forgot to open/activate it first).</summary>
        public static void RequestEdit<TKey>(string channel, TKey recordKey)
        {
            if (_channels.TryGetValue(channel, out var handler) && handler is Action<TKey> typed)
                typed.Invoke(recordKey);
        }
    }

    /// <summary>
    /// Central list of channel names so callers never hand-type string
    /// literals that could typo-drift between a report and its entry view.
    /// Add one constant per module as each gets wired up. The key shape used
    /// with each channel is documented alongside it — the channel name
    /// itself doesn't encode or enforce that, so keep Subscribe/RequestEdit
    /// calls for a given channel consistent about which TKey they use.
    /// </summary>
    public static class EntryChannels
    {
        public const string PurchaseEntry = "PurchaseEntry";               // TKey: int (PurchaseNo)
        public const string SalesEntry = "SalesEntry";                     // TKey: (string Prefix, int BillNo)
        public const string SalesReturnEntry = "SalesReturnEntry";         // TKey: int (EntryNo)
        public const string PurchaseReturnEntry = "PurchaseReturnEntry";   // TKey: int (EntryNo)
        public const string JournalEntry = "JournalEntry";                 // TKey: int (EntryNo) — journal_entry_summary
        public const string OpeningStockEntry = "OpeningStockEntry";       // TKey: string (EntryNo) — TEXT PK, not int
        public const string ExcessStockEntry = "ExcessStockEntry";         // TKey: int (EntryNo) — excess_stock_summary
        public const string ShortStockEntry = "ShortStockEntry";           // TKey: int (EntryNo) — short_stock_summary
        public const string ReceiptEntry = "ReceiptEntry";                 // TKey: int (ReceiptNo)
        public const string PaymentEntry = "PaymentEntry";                 // TKey: int (PaymentNo)
        public const string IndividualStockReport = "IndividualStockReport"; // TKey: (string ItemCode, int? BatchId) — report-to-report jump, not an edit

        // TKey: (string AcHeadCode, string AcHeadName, DateTime FromDate, DateTime ToDate)
        // — report-to-report jump, not an edit. AcHeadCode is the primary
        // match (Trial Balance always has it); AcHeadName is a fallback for
        // Balance Sheet / Trading P&L rows, which only carry a display
        // Particulars string, not an ac_head_code — see
        // LedgerReportView.OnLedgerRequested.
        public const string LedgerReport = "LedgerReport";
        // public const string AccountHeadEntry = "AccountHeadEntry";   // TKey: int (AcHeadCode)
    }
}