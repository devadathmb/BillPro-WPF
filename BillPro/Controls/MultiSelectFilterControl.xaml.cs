using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BillPro.Controls
{
    /// <summary>
    /// Backing item for the checklist popup — same shape as SeriesFilterControl's
    /// SeriesCheckItem, but generic (Value/Label instead of Prefix/series-specific
    /// Label composition), so any report can filter on any small fixed set of
    /// string values (transaction type, status, etc.) without a bespoke control.
    /// </summary>
    public class MultiCheckItem : INotifyPropertyChanged
    {
        public string Value { get; set; }
        public string Label { get; set; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Generic version of SeriesFilterControl — same toggle-button + checklist-popup
    /// UX, but LoadItems takes plain (Value, Label) pairs instead of
    /// SalesSeriesMaster, so it can back any multi-select filter (transaction
    /// type, status, category, etc.). SeriesFilterControl is left as-is for
    /// existing series filters; new reports should prefer this one.
    ///
    /// Usage:
    ///   modeFilter.LoadItems(TransactionModeOptions.All, allLabel: "All Types");
    ///   ...
    ///   var selected = modeFilter.SelectedValues; // empty = no filter
    /// </summary>
    public partial class MultiSelectFilterControl : UserControl
    {
        private List<MultiCheckItem> _items = new();
        private string _allLabel = "All";

        /// <summary>Raised whenever the checked set changes (checkbox click, All, or None).</summary>
        public event EventHandler SelectionChanged;

        public MultiSelectFilterControl()
        {
            InitializeComponent();
        }

        /// <summary>Populates the checklist from plain (Value, Label) pairs.
        /// allLabel is what the toggle button shows when nothing is checked
        /// (interpreted as "no filter" / "everything included" by convention,
        /// same as SeriesFilterControl's "All Series").</summary>
        public void LoadItems(IEnumerable<(string Value, string Label)> items, string allLabel = "All")
        {
            _allLabel = allLabel;
            _items = items
                .Select(i => new MultiCheckItem { Value = i.Value, Label = i.Label, IsChecked = false })
                .ToList();

            itemsList.ItemsSource = _items;
            UpdateLabel();
        }

        /// <summary>Empty list = no filter (everything included).</summary>
        public List<string> SelectedValues => _items.Where(i => i.IsChecked).Select(i => i.Value).ToList();

        public void SetSelectedValues(IEnumerable<string> values)
        {
            var set = new HashSet<string>(values ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
                item.IsChecked = set.Contains(item.Value);

            UpdateLabel();
        }

        private void ToggleBtn_Checked(object sender, RoutedEventArgs e) => popup.IsOpen = true;

        private void Popup_Closed(object sender, EventArgs e) => toggleBtn.IsChecked = false;

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsChecked = true;
            UpdateLabel();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsChecked = false;
            UpdateLabel();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CheckItem_Click(object sender, RoutedEventArgs e)
        {
            UpdateLabel();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateLabel()
        {
            var selected = SelectedValues;

            if (selected.Count == 0)
            {
                txtSelectedLabel.Text = _allLabel;
                return;
            }

            var labels = _items.Where(i => i.IsChecked).Select(i => i.Label).ToList();

            const int maxShown = 2;
            txtSelectedLabel.Text = labels.Count <= maxShown
                ? string.Join(", ", labels)
                : string.Join(", ", labels.Take(maxShown)) + $" (+{labels.Count - maxShown})";
        }
    }
}