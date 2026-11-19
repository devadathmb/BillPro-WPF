using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace BillPro.Controls
{
    public class SmartComboBox : ComboBox
    {
        private string _userSearchText = "";
        private bool _isNavigatingWithArrows = false;

        // ── Extra property names to include in the search filter ─────────────
        // Set this in XAML as a comma-separated list, e.g. SearchPaths="ItemName,HsnCode"
        public static readonly DependencyProperty SearchPathsProperty =
            DependencyProperty.Register("SearchPaths", typeof(string), typeof(SmartComboBox),
                new PropertyMetadata(null));

        /// <summary>
        /// Comma-separated list of property names to search in addition to
        /// DisplayMemberPath and SelectedValuePath.
        /// Example: SearchPaths="ItemName,HsnCode"
        /// </summary>    

        private class ConstantGroupDescription : GroupDescription
        {
            public override object GroupNameFromItem(object item, int level, System.Globalization.CultureInfo culture)
            {
                return "AllItems"; // Forces all items into one group unconditionally
            }
        }

        public string SearchPaths
        {
            get => (string)GetValue(SearchPathsProperty);
            set => SetValue(SearchPathsProperty, value);
        }

        private string[] GetSearchPropertyNames()
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(DisplayMemberPath))
                names.Add(DisplayMemberPath);

            if (!string.IsNullOrWhiteSpace(SelectedValuePath))
                names.Add(SelectedValuePath);

            if (!string.IsNullOrWhiteSpace(SearchPaths))
            {
                foreach (var part in SearchPaths.Split(','))
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        names.Add(trimmed);
                }
            }

            return names.ToArray();
        }

        public SmartComboBox()
        {
            this.IsEditable = true;
            this.StaysOpenOnEdit = true;
            this.IsTextSearchEnabled = false;
        }

        // 1. Native Property for jumping to the Next field
        public static readonly DependencyProperty MoveOnEnterProperty =
            DependencyProperty.Register("MoveOnEnter", typeof(bool), typeof(SmartComboBox), new PropertyMetadata(false));

        public bool MoveOnEnter
        {
            get { return (bool)GetValue(MoveOnEnterProperty); }
            set { SetValue(MoveOnEnterProperty, value); }
        }

        // 2. Native Property for jumping to a Specific control
        public static readonly DependencyProperty TargetOnEnterProperty =
            DependencyProperty.Register("TargetOnEnter", typeof(UIElement), typeof(SmartComboBox), new PropertyMetadata(null));

        public UIElement TargetOnEnter
        {
            get { return (UIElement)GetValue(TargetOnEnterProperty); }
            set { SetValue(TargetOnEnterProperty, value); }
        }

        // ── Swallow SelectionChanged while the user is just browsing with arrows ──
        protected override void OnSelectionChanged(SelectionChangedEventArgs e)
        {
            if (_isNavigatingWithArrows)
            {
                // Don't raise the event — caller only wants to highlight, not commit.
                e.Handled = true;
                return;
            }
            base.OnSelectionChanged(e);
        }

        // ==========================================
        // NEW: Open dropdown instantly on mouse click
        // ==========================================
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            // Attach a click event directly to the text area so it opens the dropdown immediately
            TextBox textBox = this.Template.FindName("PART_EditableTextBox", this) as TextBox;
            if (textBox != null)
            {
                textBox.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    if (!this.IsDropDownOpen)
                        this.IsDropDownOpen = true;
                };
            }
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);

            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                // 1. Instantly open the dropdown so they can see all options before typing
                if (!this.IsDropDownOpen)
                {
                    this.IsDropDownOpen = true;
                }

                // 2. Clear any previous filters if the text box is empty, showing the full list
                if (this.ItemsSource != null && string.IsNullOrWhiteSpace(this.Text))
                {
                    ICollectionView view = CollectionViewSource.GetDefaultView(this.ItemsSource);
                    if (view != null)
                    {
                        view.GroupDescriptions.Clear();

                        // Replace the DummyProperty line with the custom description
                        view.GroupDescriptions.Add(new ConstantGroupDescription());

                        view.Filter = null;
                        if (view is ListCollectionView listCollectionView)
                        {
                            listCollectionView.CustomSort = null;
                        }
                        view.Refresh();
                    }
                }

                // 3. Only auto-highlight text if the user reached this field by pressing TAB
                if (e.KeyboardDevice.IsKeyDown(Key.Tab))
                {
                    TextBox textBox = this.Template.FindName("PART_EditableTextBox", this) as TextBox;
                    if (textBox != null)
                    {
                        textBox.SelectAll();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                e.Handled = true;

                if (!this.IsDropDownOpen)
                {
                    this.IsDropDownOpen = true;
                    return;
                }

                if (this.Items.Count > 0)
                {
                    _isNavigatingWithArrows = true;

                    TextBox textBox = this.Template.FindName("PART_EditableTextBox", this) as TextBox;
                    int caret = textBox?.CaretIndex ?? this.Text.Length;

                    int newIndex = this.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    if (newIndex < 0) newIndex = 0;
                    if (newIndex >= this.Items.Count) newIndex = this.Items.Count - 1;

                    this.SelectedIndex = newIndex;
                    this.Text = _userSearchText;
                    if (textBox != null) textBox.CaretIndex = caret;

                    // Scroll the highlighted item into view inside the dropdown popup
                    ScrollSelectedItemIntoView(newIndex);

                    _isNavigatingWithArrows = false;
                }
            }
            else if (e.Key == Key.Return)
            {
                e.Handled = true;

                // If the user browsed with arrow keys, SelectedIndex is highlighted but
                // SelectionChanged was suppressed (so the view's handler never fired).
                // Commit it now by clearing the navigation flag and re-assigning SelectedItem.
                if (this.IsDropDownOpen && this.SelectedIndex >= 0)
                {
                    var itemToCommit = this.Items[this.SelectedIndex];
                    _isNavigatingWithArrows = false;   // allow OnSelectionChanged to fire
                    this.SelectedItem = null;           // force a change so the event triggers
                    this.SelectedItem = itemToCommit;  // this now fires SelectionChanged properly
                }
                else if (this.IsDropDownOpen && this.SelectedItem == null && !string.IsNullOrWhiteSpace(this.Text))
                {
                    // Nothing highlighted yet — auto-select the first visible item.
                    var view = CollectionViewSource.GetDefaultView(this.ItemsSource);
                    var firstItem = view?.Cast<object>().FirstOrDefault();
                    if (firstItem != null)
                        this.SelectedItem = firstItem;
                }

                if (this.SelectedItem != null)
                {
                    string displayValue = "";

                    // Priority 1: DisplayMemberPath (e.g. "CustomerName")
                    if (!string.IsNullOrEmpty(this.DisplayMemberPath))
                    {
                        var prop = this.SelectedItem.GetType().GetProperty(this.DisplayMemberPath);
                        displayValue = prop?.GetValue(this.SelectedItem, null)?.ToString() ?? "";
                    }
                    else
                    {
                        // Priority 2: TextSearch.TextPath — used by multi-column ItemTemplate combos
                        // where DisplayMemberPath is intentionally left empty.
                        string textPath = TextSearch.GetTextPath(this);
                        if (!string.IsNullOrEmpty(textPath))
                        {
                            var prop = this.SelectedItem.GetType().GetProperty(textPath);
                            displayValue = prop?.GetValue(this.SelectedItem, null)?.ToString() ?? "";
                        }
                    }

                    // Priority 3: fall back to ToString() only if both paths are absent/empty
                    if (string.IsNullOrEmpty(displayValue))
                        displayValue = this.SelectedItem.ToString() ?? "";

                    this.Text = displayValue;
                    _userSearchText = displayValue;
                }

                this.IsDropDownOpen = false;

                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (this.TargetOnEnter != null)
                    {
                        this.TargetOnEnter.Focus();
                        if (this.TargetOnEnter is TextBox tb) tb.SelectAll();
                    }
                    else if (this.MoveOnEnter)
                    {
                        UIElement activeElement = Keyboard.FocusedElement as UIElement;
                        if (activeElement != null)
                        {
                            activeElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                base.OnPreviewKeyDown(e);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            if (e.Key == Key.Return || e.Key == Key.Tab || e.Key == Key.Escape ||
                e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right ||
                e.Key == Key.System)
            {
                return;
            }

            // 1. Grab the internal textbox and cache text
            TextBox textBox = this.Template.FindName("PART_EditableTextBox", this) as TextBox;
            string currentText = this.Text ?? "";
            int caretIndex = textBox?.CaretIndex ?? currentText.Length;

            // FIX: Open the dropdown BEFORE wiping SelectedItem. 
            // If you open it after, WPF runs an internal routine that erases the letter you just typed!
            if (!this.IsDropDownOpen)
            {
                this.IsDropDownOpen = true;
            }

            if (!_isNavigatingWithArrows)
            {
                // Clear the selection (This normally wipes the text box)
                this.SelectedItem = null;

                // Instantly restore the text and cursor position
                this.Text = currentText;
                if (textBox != null)
                {
                    textBox.CaretIndex = caretIndex;
                }

                _userSearchText = this.Text;
            }

            if (this.ItemsSource == null) return;

            string searchText = _userSearchText.ToLower();
            ICollectionView view = CollectionViewSource.GetDefaultView(this.ItemsSource);

            if (view != null)
            {
                // Capture once so the lambda doesn't re-evaluate on every item
                var searchPropertyNames = GetSearchPropertyNames();

                view.Filter = item =>
                {
                    if (string.IsNullOrEmpty(searchText)) return true;

                    if (item == null) return false;

                    // If no named properties configured, fall back to ToString()
                    if (searchPropertyNames.Length == 0)
                        return item.ToString()?.ToLower().Contains(searchText) ?? false;

                    var itemType = item.GetType();
                    foreach (var propName in searchPropertyNames)
                    {
                        var prop = itemType.GetProperty(propName);
                        var val = prop?.GetValue(item, null)?.ToString()?.ToLower() ?? "";
                        if (val.Contains(searchText)) return true;
                    }
                    return false;
                };

                if (view is ListCollectionView listCollectionView)
                {
                    // Pass DisplayMemberPath first; fall back to TextSearch.TextPath so the
                    // entry-line combo (which uses ItemTemplate, not DisplayMemberPath) also sorts.
                    string sortPath = !string.IsNullOrEmpty(this.DisplayMemberPath)
                        ? this.DisplayMemberPath
                        : TextSearch.GetTextPath(this);

                    listCollectionView.CustomSort = string.IsNullOrEmpty(searchText)
                        ? null
                        : new SmartSorter(searchText, sortPath);
                }

                view.Refresh();
            }
        }

        /// <summary>
        /// Walks the ComboBox popup's visual tree to find its internal ScrollViewer,
        /// then brings the item at <paramref name="index"/> into view.
        /// This must be deferred with BeginInvoke because the ItemContainerGenerator
        /// may not have materialised the container yet at the moment SelectedIndex changes.
        /// </summary>
        private void ScrollSelectedItemIntoView(int index)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                // The popup items live inside a PART_Popup → ScrollViewer → ItemsPresenter chain.
                // The easiest entry-point is the ItemContainerGenerator on the ComboBox itself.
                if (index < 0 || index >= this.Items.Count) return;

                // Walk the template to find the dropdown's ItemsPresenter/Panel
                var popup = this.Template?.FindName("PART_Popup", this) as Popup;
                if (popup?.Child == null) return;

                var scrollViewer = FindVisualChild<ScrollViewer>(popup.Child);
                if (scrollViewer == null) return;

                var itemsPresenter = FindVisualChild<ItemsPresenter>(scrollViewer);
                if (itemsPresenter == null) return;

                // The VirtualizingStackPanel (or StackPanel) is the direct child of ItemsPresenter
                var panel = VisualTreeHelper.GetChild(itemsPresenter, 0) as Panel;
                if (panel == null) return;

                // Get the container and bring it into view
                var container = this.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                container?.BringIntoView();

            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Finds the first visual child of type <typeparamref name="T"/> in the tree.</summary>
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private class SmartSorter : System.Collections.IComparer
        {
            private readonly string _searchText;
            private readonly string _displayMemberPath;
            private PropertyInfo _propInfo;

            public SmartSorter(string searchText, string displayMemberPath)
            {
                _searchText = searchText;
                _displayMemberPath = displayMemberPath;
            }

            // Three-tier ranking:
            //   0 = exact match        e.g. search "special" → "Special Oil Bottle 1L"
            //                               whose NAME starts with the word "Special"
            //         actually: exact word-start: any word in the value starts with searchText
            //   Tier 0 — value equals searchText exactly
            //   Tier 1 — value starts with searchText
            //   Tier 2 — any word inside the value starts with searchText
            //   Tier 3 — value merely contains searchText somewhere
            private int GetTier(string value)
            {
                if (value == _searchText) return 0;
                if (value.StartsWith(_searchText)) return 1;

                // Check if any word boundary within the value starts with the search text
                // e.g. "Special Oil Bottle" — the word "Special" starts with "special"
                int idx = 0;
                while ((idx = value.IndexOf(_searchText, idx)) >= 0)
                {
                    if (idx == 0 || value[idx - 1] == ' ')
                        return 2;
                    idx++;
                }

                return 3; // just a mid-word contain
            }

            public int Compare(object x, object y)
            {
                string valX = GetStringValue(x);
                string valY = GetStringValue(y);

                int tierX = GetTier(valX);
                int tierY = GetTier(valY);

                if (tierX != tierY) return tierX.CompareTo(tierY);

                // Within the same tier, sort alphabetically
                return string.Compare(valX, valY, StringComparison.OrdinalIgnoreCase);
            }

            private string GetStringValue(object item)
            {
                if (item == null) return "";
                if (string.IsNullOrEmpty(_displayMemberPath)) return item.ToString().ToLower();

                if (_propInfo == null)
                {
                    _propInfo = item.GetType().GetProperty(_displayMemberPath);
                }

                return _propInfo?.GetValue(item, null)?.ToString().ToLower() ?? "";
            }
        }
    }
}