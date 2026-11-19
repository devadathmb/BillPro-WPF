using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BillPro.Helpers;

namespace BillPro.Controls
{
    public partial class GenericSearchOverlay : UserControl
    {
        public event EventHandler<object> ItemSelected;
        public event EventHandler Closed;

        private SearchConfig _currentConfig;
        private PropertyInfo[] _cachedSearchProperties;
        private ICollectionView _view;

        public GenericSearchOverlay()
        {
            InitializeComponent();

        }

        public void LoadData(IEnumerable<object> items, SearchConfig config)
        {
            _currentConfig = config;
            txtTitle.Text = config.Title;

            // 1. Handle Date Filter Visibility & Defaults
            if (config.ShowDateFilter)
            {
                pnlDateFilter.Visibility = Visibility.Visible;

                // Disable events while setting defaults to avoid unnecessary refresh cycles
                dpFromDate.SelectedDateChanged -= DateFilter_Changed;
                dpToDate.SelectedDateChanged -= DateFilter_Changed;

                dpFromDate.SelectedDate = DateTime.Today;
                dpToDate.SelectedDate = DateTime.Today;
                chkShowAll.IsChecked = false;

                dpFromDate.SelectedDateChanged += DateFilter_Changed;
                dpToDate.SelectedDateChanged += DateFilter_Changed;
            }
            else
            {
                pnlDateFilter.Visibility = Visibility.Collapsed;
            }

            // 2. Build DataGrid Columns Dynamically
            dgItems.Columns.Clear();
            var lengthConverter = new DataGridLengthConverter();

            foreach (var col in config.Columns)
            {
                var binding = new Binding(col.BindingPath);
                if (!string.IsNullOrEmpty(col.StringFormat))
                    binding.StringFormat = col.StringFormat;

                var gridCol = new DataGridTextColumn
                {
                    Header = col.Header,
                    Binding = binding,
                    Width = (DataGridLength)lengthConverter.ConvertFromString(col.Width)
                };
                dgItems.Columns.Add(gridCol);
            }

            // 3. Cache PropertyInfo for Search
            var firstItem = items.FirstOrDefault();
            if (firstItem != null && config.SearchProperties.Any())
            {
                var itemType = firstItem.GetType();
                _cachedSearchProperties = config.SearchProperties
                    .Select(prop => itemType.GetProperty(prop))
                    .Where(p => p != null)
                    .ToArray();
            }
            else
            {
                _cachedSearchProperties = Array.Empty<PropertyInfo>();
            }

            // 4. Load Data & Reset UI
            dgItems.ItemsSource = items;
            txtSearch.Text = string.Empty;
            chkShowAll.IsChecked = false;

            // 5. Setup CollectionView
            if (items != null)
            {
                _view = CollectionViewSource.GetDefaultView(dgItems.ItemsSource);
                _view.Filter = CombinedFilter;
            }

            // 6. Set Focus
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                txtSearch.Focus();
                Keyboard.Focus(txtSearch);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // ==========================================
        // COMBINED FILTER LOGIC (Text + Date + ShowAll)
        // ==========================================
        private bool CombinedFilter(object item)
        {
            // 1. TEXT FILTER: Pass if it matches
            if (!IsTextMatch(item)) return false;

            // 2. DATE FILTER: Pass if ShowAll is true OR date is within range
            if (chkShowAll.IsChecked == true) return true;

            if (_currentConfig.ShowDateFilter && !string.IsNullOrEmpty(_currentConfig.DateFilterProperty))
            {
                var dateProp = item.GetType().GetProperty(_currentConfig.DateFilterProperty);
                if (dateProp != null)
                {
                    var itemDate = (DateTime?)dateProp.GetValue(item);
                    if (itemDate.HasValue)
                    {
                        if (dpFromDate.SelectedDate.HasValue && itemDate.Value.Date < dpFromDate.SelectedDate.Value.Date)
                            return false;
                        if (dpToDate.SelectedDate.HasValue && itemDate.Value.Date > dpToDate.SelectedDate.Value.Date)
                            return false;
                    }
                }
            }
            return true;
        }

        private bool IsTextMatch(object item)
        {
            string searchText = txtSearch.Text?.ToLower() ?? "";
            if (string.IsNullOrWhiteSpace(searchText)) return true;
            if (_cachedSearchProperties == null || _cachedSearchProperties.Length == 0) return true;

            foreach (var prop in _cachedSearchProperties)
            {
                string value = prop.GetValue(item)?.ToString()?.ToLower() ?? "";
                if (value.Contains(searchText)) return true;
            }
            return false;
        }

        // ==========================================
        // Event Handlers
        // ==========================================
        private void DateFilter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

        private void txtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_view == null || _currentConfig == null) return;

            string searchText = txtSearch.Text?.ToLower() ?? "";

            // Apply priority sort
            if (_view is ListCollectionView listCollectionView)
            {
                listCollectionView.CustomSort = string.IsNullOrWhiteSpace(searchText)
                    ? null
                    : new GenericSorter(searchText, _cachedSearchProperties);
            }
            _view.Refresh();
        }

        // ==========================================
        // Sorter & Selection Logic
        // ==========================================
        private class GenericSorter : System.Collections.IComparer
        {
            private readonly string _searchText;
            private readonly PropertyInfo[] _searchProperties;

            public GenericSorter(string searchText, PropertyInfo[] searchProperties)
            {
                _searchText = searchText;
                _searchProperties = searchProperties ?? Array.Empty<PropertyInfo>();
            }

            public int Compare(object x, object y)
            {
                bool xStarts = StartsWithAny(x);
                bool yStarts = StartsWithAny(y);

                if (xStarts && !yStarts) return -1;
                if (!xStarts && yStarts) return 1;

                if (_searchProperties.Length > 0)
                {
                    string valX = GetStringValue(x, _searchProperties[0]);
                    string valY = GetStringValue(y, _searchProperties[0]);
                    return string.Compare(valX, valY, StringComparison.OrdinalIgnoreCase);
                }
                return 0;
            }

            private bool StartsWithAny(object item)
            {
                foreach (var prop in _searchProperties)
                {
                    string val = GetStringValue(item, prop);
                    if (val.StartsWith(_searchText)) return true;
                }
                return false;
            }

            private string GetStringValue(object item, PropertyInfo prop) =>
                prop?.GetValue(item, null)?.ToString()?.ToLower() ?? "";
        }

        private void ConfirmSelection()
        {
            if (dgItems.SelectedItem != null)
            {
                ItemSelected?.Invoke(this, dgItems.SelectedItem);
                this.Visibility = Visibility.Collapsed;

                if (_currentConfig?.TargetOnSelect != null)
                {
                    this.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _currentConfig.TargetOnSelect.Focus();
                        if (_currentConfig.TargetOnSelect is TextBox tb) tb.SelectAll();
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
            }
            else
            {
                MessageBox.Show("Please select an item first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseOverlay()
        {
            Closed?.Invoke(this, EventArgs.Empty);
            this.Visibility = Visibility.Collapsed;
        }

        private void btnContinue_Click(object sender, RoutedEventArgs e) => ConfirmSelection();
        private void dgItems_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();
        private void btnClose_Click(object sender, RoutedEventArgs e) => CloseOverlay();

        private void txtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (dgItems.Items.Count > 0)
                {
                    dgItems.SelectedIndex = Math.Min(dgItems.Items.Count - 1, dgItems.SelectedIndex + 1);
                    dgItems.ScrollIntoView(dgItems.SelectedItem);
                }
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (dgItems.Items.Count > 0)
                {
                    dgItems.SelectedIndex = Math.Max(0, dgItems.SelectedIndex - 1);
                    dgItems.ScrollIntoView(dgItems.SelectedItem);
                }
            }
            else if (e.Key == Key.Return)
            {
                e.Handled = true;
                if (dgItems.SelectedItem == null && dgItems.Items.Count > 0 && !string.IsNullOrWhiteSpace(txtSearch.Text))
                    dgItems.SelectedIndex = 0;
                ConfirmSelection();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseOverlay();
            }
        }
    }
}