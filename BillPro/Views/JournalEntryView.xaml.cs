using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BillPro.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Free-form double-entry Journal Entry — any number of Debit/Credit
    // lines across any a/c heads. See JournalEntryRepository.Accounts.cs
    // for how this posts (every line becomes its own SettlementLeg, no tax
    // concept at all).
    public partial class JournalEntryView : UserControl, IDisposable
    {
        private readonly JournalEntryRepository _repository;
        private readonly AccountHeadRepository _acHeadRepository;

        private const string CurrentUserId = "ADMIN";

        private readonly ObservableCollection<JournalGridLine> _lines = new();

        private bool _isEditMode;
        private int _currentEntryNo;

        public JournalEntryView()
        {
            InitializeComponent();

            _repository = new JournalEntryRepository();
            _repository.InitializeDatabase();

            _acHeadRepository = new AccountHeadRepository();

            sfLines.ItemsSource = _lines;
            _lines.CollectionChanged += (s, e) =>
            {
                // Keep totals in sync with in-grid edits too, not just
                // additions/removals — subscribe/unsubscribe per row as
                // lines come and go so edited Amount/Mode values are
                // reflected immediately.
                if (e.OldItems != null)
                    foreach (JournalGridLine line in e.OldItems)
                        line.PropertyChanged -= Line_PropertyChanged;
                if (e.NewItems != null)
                    foreach (JournalGridLine line in e.NewItems)
                        line.PropertyChanged += Line_PropertyChanged;

                RecalculateTotals();
            };

            LoadAcHeadCombo();
            LoadNarrationSuggestions();

            ClearFields();
            ToggleFields(false);
            btnSave.IsEnabled = false;
            btnDelete.IsEnabled = false;

            // Bridge for the Daybook/Ledger Report's "double-click a Journal
            // row to edit it" — see EntryEditNavigator's class doc comment
            // for why this is a Subscribe-in-constructor/Unsubscribe-in-
            // Dispose pair rather than Loaded/Unloaded.
            EntryEditNavigator.Subscribe<int>(EntryChannels.JournalEntry, OnEditRequested);
        }

        private void OnEditRequested(int entryNo) => LoadEntryForView(entryNo);

        public void Dispose() =>
            EntryEditNavigator.Unsubscribe<int>(EntryChannels.JournalEntry, OnEditRequested);

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => btnNew.Focus()));
        }

        private void LoadAcHeadCombo()
        {
            cmbEntryAcHead.ItemsSource = _acHeadRepository.GetAllForLookup();
        }

        // Free-typed-but-suggested narration — the "memory saving textbox"
        // alternative: a plain editable SmartComboBox whose suggestion list
        // is every narration ever typed on a prior journal line, most
        // recent first. Typing something brand new is always accepted —
        // Text is read directly at Add time, nothing requires a selection.
        private void LoadNarrationSuggestions()
        {
            cmbEntryNarration.ItemsSource = _repository.GetDistinctNarrations();
        }

        private void ToggleFields(bool enabled)
        {
            dpEntryDate.IsEnabled = enabled;
            cmbEntryMode.IsEnabled = enabled;
            cmbEntryAcHead.IsEnabled = enabled;
            cmbEntryNarration.IsEnabled = enabled;
            txtEntryAmount.IsEnabled = enabled;
            btnAddLine.IsEnabled = enabled;
        }

        private void ClearFields()
        {
            txtEntryNo.Text = _repository.GetNextEntryNo().ToString();
            dpEntryDate.SelectedDate = DateTime.Now;
            cmbEntryMode.SelectedIndex = 0;
            cmbEntryAcHead.SelectedIndex = -1;
            cmbEntryAcHead.Text = "";
            cmbEntryNarration.SelectedIndex = -1;
            cmbEntryNarration.Text = "";
            txtEntryAmount.Text = "0";

            _lines.Clear();

            _isEditMode = false;
            _currentEntryNo = 0;
            btnSave.Content = "_Save";
        }

        // ── New ──────────────────────────────────────────────────────────
        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            ClearFields();
            ToggleFields(true);
            btnSave.IsEnabled = false; // re-enabled once at least one balanced line exists
            btnDelete.IsEnabled = false;
            dpEntryDate.Focus();
        }

        // ── Add a line to the grid ──────────────────────────────────────
        private void BtnAddLine_Click(object sender, RoutedEventArgs e)
        {
            if (!(cmbEntryAcHead.SelectedItem is AccountHeadMaster selectedHead))
            {
                MessageBox.Show("Please select an Account Head!", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbEntryAcHead.Focus();
                return;
            }

            if (!decimal.TryParse(txtEntryAmount.Text, out decimal amount) || amount <= 0)
            {
                MessageBox.Show("Please enter a valid Amount!", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtEntryAmount.Focus();
                return;
            }

            string rawMode = (cmbEntryMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Debit";
            // Combo labels ("Debit/Payment", "Credit/Receipt") are just display
            // text — JournalGridLine and the repository/posting code only ever
            // key off the literal "Debit"/"Credit", so normalize here rather
            // than depending on the label wording.
            string mode = rawMode.StartsWith("Credit", StringComparison.OrdinalIgnoreCase) ? "Credit" : "Debit";
            string narration = cmbEntryNarration.Text?.Trim();

            _lines.Add(new JournalGridLine
            {
                Mode = mode,
                AcHeadCode = selectedHead.AcHeadCode,
                AcHeadName = selectedHead.AcHeadName,
                Narration = narration,
                Amount = amount
            });

            // A brand-new narration should be offered as a suggestion for
            // the next line/voucher without waiting for a Save round-trip.
            if (!string.IsNullOrEmpty(narration) &&
                !((IEnumerable<string>)cmbEntryNarration.ItemsSource ?? Enumerable.Empty<string>()).Contains(narration))
            {
                var updated = new List<string> { narration };
                updated.AddRange((IEnumerable<string>)cmbEntryNarration.ItemsSource ?? Enumerable.Empty<string>());
                cmbEntryNarration.ItemsSource = updated;
            }

            cmbEntryAcHead.SelectedIndex = -1;
            cmbEntryAcHead.Text = "";
            cmbEntryNarration.SelectedIndex = -1;
            cmbEntryNarration.Text = "";
            // Amount is intentionally left as-is (not reset to "0") — journal
            // entries commonly need the same amount split across a few
            // consecutive lines, so retaining it saves re-typing.
            cmbEntryAcHead.Focus();
        }

        private void Line_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(JournalGridLine.DebitAmount) ||
                e.PropertyName == nameof(JournalGridLine.CreditAmount) ||
                e.PropertyName == nameof(JournalGridLine.Amount) ||
                e.PropertyName == nameof(JournalGridLine.Mode))
                RecalculateTotals();
        }

        private void BtnDeleteLine_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is JournalGridLine line)
                _lines.Remove(line);
        }

        // ── Totals / balance check ───────────────────────────────────────
        private void RecalculateTotals()
        {
            decimal totalDebit = _lines.Sum(l => l.DebitAmount);
            decimal totalCredit = _lines.Sum(l => l.CreditAmount);

            lblTotalDebit.Text = totalDebit.ToString("N2");
            lblTotalCredit.Text = totalCredit.ToString("N2");

            bool balanced = _lines.Count > 0 && totalDebit == totalCredit && totalDebit > 0;
            lblBalanceWarning.Visibility = (!balanced && _lines.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

            // Only offered while actively editing (New or a re-opened,
            // non-cancelled entry) — SetCancelledState/ToggleFields(false)
            // after a successful Save/Update or on a cancelled voucher
            // still wins regardless of what this sets.
            if (dpEntryDate.IsEnabled)
                btnSave.IsEnabled = balanced;
        }

        // ── Save / Update ────────────────────────────────────────────────
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isEditMode && !BillPro.Helpers.PermissionGate.RequireEdit()) return;
            if (dpEntryDate.SelectedDate == null)
            {
                MessageBox.Show("Please select a valid voucher date!", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            decimal totalDebit = _lines.Sum(l => l.DebitAmount);
            decimal totalCredit = _lines.Sum(l => l.CreditAmount);

            if (_lines.Count == 0 || totalDebit != totalCredit || totalDebit == 0)
            {
                MessageBox.Show("Total Debit and Total Credit must match (and be non-zero) before saving.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var header = new JournalEntrySummary
            {
                EntryNo = _currentEntryNo,
                EntryDate = dpEntryDate.SelectedDate.Value.ToString("yyyy-MM-dd"),
                TotalAmount = (double)totalDebit
            };

            var lines = _lines.Select(l => new JournalEntryLine
            {
                Mode = l.Mode,
                AcHeadCode = l.AcHeadCode,
                AcHeadName = l.AcHeadName,
                Narration = l.Narration,
                Amount = (double)l.Amount
            }).ToList();

            try
            {
                if (!_isEditMode)
                {
                    header.CreatedBy = CurrentUserId;
                    header.CreatedDate = DateTime.Now.ToString("s");
                    _repository.Save(header, lines, CurrentUserId);
                    MessageBox.Show("Journal Voucher Saved Successfully.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    header.LastModifiedBy = CurrentUserId;
                    header.LastModifiedDate = DateTime.Now.ToString("s");
                    _repository.Update(header, lines, CurrentUserId);
                    MessageBox.Show("Journal Voucher Updated Successfully.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _currentEntryNo = header.EntryNo;
                _isEditMode = true;
                txtEntryNo.Text = header.EntryNo.ToString();

                ToggleFields(false);
                btnSave.IsEnabled = false;
                btnDelete.IsEnabled = false;
                btnNew.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving journal voucher:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Display / Load for view ─────────────────────────────────────
        private void BtnDisplay_Click(object sender, RoutedEventArgs e)
        {
            var allItems = _repository.GetAll();
            var config = new SearchConfig
            {
                Title = "Search Journal Vouchers",
                SearchProperties = new List<string> { "EntryDate" },
                Columns = new List<SearchColumn>
                {
                    new SearchColumn { Header = "Voucher No", BindingPath = "EntryNo", Width = "90" },
                    new SearchColumn { Header = "Date", BindingPath = "EntryDate", Width = "100" },
                    new SearchColumn { Header = "Amount", BindingPath = "TotalAmount", Width = "100" },
                },
                TargetOnSelect = null
            };
            SearchOverlay.LoadData(allItems, config);
            SearchOverlay.ItemSelected += SearchOverlay_ItemSelected;
            SearchOverlay.Closed += SearchOverlay_Closed;
            SearchOverlay.Visibility = Visibility.Visible;
        }

        private void SearchOverlay_ItemSelected(object sender, object e)
        {
            if (e is JournalEntrySummary selected)
                LoadEntryForView(selected.EntryNo);
        }

        private void SearchOverlay_Closed(object sender, EventArgs e)
        {
            btnDisplay.Focus();
        }

        private void LoadEntryForView(int entryNo)
        {
            var header = _repository.GetByEntryNo(entryNo);
            if (header == null) return;

            var lines = _repository.GetLines(entryNo);

            try
            {
                _isEditMode = true;
                _currentEntryNo = header.EntryNo;

                txtEntryNo.Text = header.EntryNo.ToString();
                dpEntryDate.SelectedDate = DateTime.TryParse(header.EntryDate, out var d) ? d : (DateTime?)null;

                _lines.Clear();
                foreach (var line in lines)
                {
                    _lines.Add(new JournalGridLine
                    {
                        Mode = line.Mode,
                        AcHeadCode = line.AcHeadCode,
                        AcHeadName = line.AcHeadName,
                        Narration = line.Narration,
                        Amount = (decimal)line.Amount
                    });
                }

                bool cancelled = header.CancelStatus == 1;

                ToggleFields(!cancelled);
                btnSave.Content = "_Update";
                btnSave.IsEnabled = true; // RecalculateTotals will re-enable if balanced and not cancelled
                btnDelete.IsEnabled = !cancelled;

                dpEntryDate.Focus();
            }
            finally
            {
            }
        }

        // ── Delete (= cancel) a displayed voucher ────────────────────────
        // Same semantics as every other entry form's "Delete"/"Cancel" —
        // only active when an existing, non-cancelled voucher is on
        // screen; reverses its accounts postings rather than removing the
        // row outright, so the audit trail (voucher_tax_postings) stays
        // intact.
        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireDelete()) return;
            if (_currentEntryNo == 0) return;

            var ans = MessageBox.Show("The selected journal voucher will be cancelled!\n\nAre you sure?",
                "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (ans == MessageBoxResult.Yes)
            {
                _repository.CancelEntry(_currentEntryNo);
                MessageBox.Show("Journal Voucher Cancelled.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                ToggleFields(false);
                btnSave.IsEnabled = false;
                btnDelete.IsEnabled = false;
            }
        }
    }
}