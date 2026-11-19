using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BillPro.Helpers
{
    /// <summary>
    /// Attached behavior that makes the editable TextBox inside a DatePicker
    /// work in "overwrite / typewriter" mode:  typing a digit replaces the
    /// character at the caret position instead of inserting before it.
    ///
    /// Usage in XAML:
    ///   helpers:DatePickerOverwriteBehavior.IsEnabled="True"
    /// </summary>
    public static class DatePickerOverwriteBehavior
    {
        // ── Attached property ────────────────────────────────────────────────
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DatePickerOverwriteBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj)
            => (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value)
            => obj.SetValue(IsEnabledProperty, value);

        // ── Wiring ──────────────────────────────────────────────────────────
        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not DatePicker dp) return;

            if ((bool)e.NewValue)
            {
                // Hook after the template is applied so PART_TextBox exists
                dp.Loaded += DatePicker_Loaded;

                // If it was already loaded (e.g. set in code-behind after Loaded)
                if (dp.IsLoaded)
                    AttachToInnerTextBox(dp);
            }
            else
            {
                dp.Loaded -= DatePicker_Loaded;
                DetachFromInnerTextBox(dp);
            }
        }

        private static void DatePicker_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is DatePicker dp)
                AttachToInnerTextBox(dp);
        }

        // ── Find the inner TextBox and hook its events ────────────────────
        private static void AttachToInnerTextBox(DatePicker dp)
        {
            var tb = FindInnerTextBox(dp);
            if (tb == null) return;

            tb.PreviewKeyDown -= InnerTextBox_PreviewKeyDown;
            tb.PreviewKeyDown += InnerTextBox_PreviewKeyDown;
        }

        private static void DetachFromInnerTextBox(DatePicker dp)
        {
            var tb = FindInnerTextBox(dp);
            if (tb == null) return;
            tb.PreviewKeyDown -= InnerTextBox_PreviewKeyDown;
        }

        private static DatePickerTextBox FindInnerTextBox(DatePicker dp)
            => dp.Template?.FindName("PART_TextBox", dp) as DatePickerTextBox;

        // ── The overwrite logic ──────────────────────────────────────────────
        private static void InnerTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not DatePickerTextBox tb) return;

            // Only intercept digit keys (top row + numpad)
            if (!IsDigitKey(e.Key)) return;

            string digit = KeyToDigit(e.Key);
            if (digit == null) return;

            int caret = tb.CaretIndex;
            string text = tb.Text;

            // Skip over separator characters (/ . - space) automatically
            while (caret < text.Length && !char.IsDigit(text[caret]))
                caret++;

            if (caret >= text.Length) return;  // past end — let normal handling run

            // Replace the character at caret with the typed digit
            tb.Text = text.Substring(0, caret) + digit + text.Substring(caret + 1);

            // Advance caret — skip the next separator if any
            int next = caret + 1;
            while (next < tb.Text.Length && !char.IsDigit(tb.Text[next]))
                next++;

            tb.CaretIndex = next;

            // Commit the text so the DatePicker parses it
            tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

            e.Handled = true;   // suppress the default insert behaviour
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static bool IsDigitKey(Key key)
            => (key >= Key.D0 && key <= Key.D9)
            || (key >= Key.NumPad0 && key <= Key.NumPad9);

        private static string KeyToDigit(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
                return ((int)(key - Key.D0)).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return ((int)(key - Key.NumPad0)).ToString();
            return null;
        }
    }
}