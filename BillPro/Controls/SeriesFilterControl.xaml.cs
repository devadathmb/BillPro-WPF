using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BillPro.Models;

namespace BillPro.Controls
{
    /// <summary>
    /// Backing item for the checklist popup. Label combines the prefix with
    /// a short descriptor (customer/transaction type) so two similarly
    /// named series are still distinguishable in the dropdown.
    /// </summary>
    public class SeriesCheckItem : INotifyPropertyChanged
    {
        public string Prefix { get; set; }
        public string Label { get; set; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class SeriesFilterControl : UserControl
    {
        private List<SeriesCheckItem> _items = new();

        /// <summary>Raised whenever the checked set changes (checkbox click, All, or None).</summary>
        public event EventHandler SelectionChanged;

        public SeriesFilterControl()
        {
            InitializeComponent();
        }

        /// <summary>Populates the checklist from the active sales series. Call once after construction
        /// (and again if series config changes) — e.g. seriesFilter.LoadSeries(_seriesRepo.GetActive());</summary>
        public void LoadSeries(List<SalesSeriesMaster> series)
        {
            _items = series
                .Select(s => new SeriesCheckItem
                {
                    Prefix = s.Prefix,
                    Label = $"{s.Prefix} — {s.CustomerType}/{s.TransactionType}",
                    IsChecked = false
                })
                .ToList();

            itemsList.ItemsSource = _items;
            UpdateLabel();
        }

        /// <summary>Empty list = no filter (all series included).</summary>
        public List<string> SelectedPrefixes => _items.Where(i => i.IsChecked).Select(i => i.Prefix).ToList();

        /// <summary>Lets a caller preselect series (e.g. restoring a saved filter). Empty/null clears selection.</summary>
        public void SetSelectedPrefixes(IEnumerable<string> prefixes)
        {
            var set = new HashSet<string>(prefixes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
                item.IsChecked = set.Contains(item.Prefix);

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

        // The small "label box": shows "All Series" when nothing is picked
        // (matches "no filter" semantics), the single prefix when exactly
        // one is picked, or a comma list with an overflow "(+N)" once it
        // would otherwise get too wide for the button.
        private void UpdateLabel()
        {
            var selected = SelectedPrefixes;

            if (selected.Count == 0)
            {
                txtSelectedLabel.Text = "All Series";
                return;
            }

            const int maxShown = 2;
            if (selected.Count <= maxShown)
            {
                txtSelectedLabel.Text = string.Join(", ", selected);
            }
            else
            {
                txtSelectedLabel.Text = string.Join(", ", selected.Take(maxShown)) + $" (+{selected.Count - maxShown})";
            }
        }
    }
}