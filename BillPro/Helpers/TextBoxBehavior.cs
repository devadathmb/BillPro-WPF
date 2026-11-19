using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BillPro.Helpers
{
    public static class TextBoxBehavior
    {
        #region IsDecimal Behavior

        public static readonly DependencyProperty IsDecimalProperty =
            DependencyProperty.RegisterAttached(
                "IsDecimal",
                typeof(bool),
                typeof(TextBoxBehavior),
                new UIPropertyMetadata(false, OnIsDecimalChanged));

        public static bool GetIsDecimal(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsDecimalProperty);
        }

        public static void SetIsDecimal(DependencyObject obj, bool value)
        {
            obj.SetValue(IsDecimalProperty, value);
        }

        // Companion flag for IsDecimal — when true, a single leading '-' is
        // accepted (typed or pasted) so the field can hold negative amounts
        // (e.g. a negative rounding adjustment). Doesn't do anything on its
        // own; only read while IsDecimal's input filtering is active.
        public static readonly DependencyProperty AllowNegativeProperty =
            DependencyProperty.RegisterAttached(
                "AllowNegative",
                typeof(bool),
                typeof(TextBoxBehavior),
                new UIPropertyMetadata(false));

        public static bool GetAllowNegative(DependencyObject obj)
        {
            return (bool)obj.GetValue(AllowNegativeProperty);
        }

        public static void SetAllowNegative(DependencyObject obj, bool value)
        {
            obj.SetValue(AllowNegativeProperty, value);
        }

        private static void OnIsDecimalChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox textBox)
            {
                if ((bool)e.NewValue)
                {
                    // Hook into the input and paste events
                    textBox.PreviewTextInput += TextBox_PreviewTextInput;
                    DataObject.AddPastingHandler(textBox, TextBox_Pasting);
                }
                else
                {
                    // Clean up if it's turned off
                    textBox.PreviewTextInput -= TextBox_PreviewTextInput;
                    DataObject.RemovePastingHandler(textBox, TextBox_Pasting);
                }
            }
        }

        private static void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = (TextBox)sender;
            string currentText = textBox.Text;
            string textToInsert = e.Text;

            // Minus sign: only allowed as the very first character, only once,
            // and only when AllowNegative is set on this TextBox.
            if (textToInsert == "-")
            {
                bool allowNegative = GetAllowNegative(textBox);
                bool atStart = textBox.SelectionStart == 0;
                bool alreadyHasMinus = currentText.Contains("-");

                if (!allowNegative || !atStart || alreadyHasMinus)
                    e.Handled = true;

                return;
            }

            // Allow only one decimal point
            if (textToInsert == ".")
            {
                if (currentText.Contains("."))
                {
                    e.Handled = true; // Reject second dot
                    return;
                }
            }
            // Reject any character that is not a digit
            else if (!char.IsDigit(textToInsert, 0))
            {
                e.Handled = true;
            }
        }

        private static void TextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string pastedText = (string)e.DataObject.GetData(typeof(string));
                var textBox = (TextBox)sender;

                // Simulate what the text would look like after pasting
                string newText = textBox.Text
                    .Remove(textBox.SelectionStart, textBox.SelectionLength)
                    .Insert(textBox.SelectionStart, pastedText);

                // If the resulting text isn't a valid decimal, cancel the paste.
                // Also block negative values unless this field explicitly allows them.
                bool allowNegative = GetAllowNegative(textBox);
                if (!decimal.TryParse(newText, out decimal parsed) || (parsed < 0 && !allowNegative))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        #endregion

        #region SelectAllOnFocus Behavior

        public static readonly DependencyProperty SelectAllOnFocusProperty =
            DependencyProperty.RegisterAttached(
                "SelectAllOnFocus",
                typeof(bool),
                typeof(TextBoxBehavior),
                new UIPropertyMetadata(false, OnSelectAllOnFocusChanged));

        public static bool GetSelectAllOnFocus(TextBox element)
        {
            return (bool)element.GetValue(SelectAllOnFocusProperty);
        }

        public static void SetSelectAllOnFocus(TextBox element, bool value)
        {
            element.SetValue(SelectAllOnFocusProperty, value);
        }

        private static void OnSelectAllOnFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox textBox)
            {
                if ((bool)e.NewValue)
                {
                    textBox.GotFocus += TextBox_GotFocus;
                    textBox.PreviewMouseLeftButtonDown += TextBox_PreviewMouseLeftButtonDown;
                }
                else
                {
                    textBox.GotFocus -= TextBox_GotFocus;
                    textBox.PreviewMouseLeftButtonDown -= TextBox_PreviewMouseLeftButtonDown;
                }
            }
        }

        private static void TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            (sender as TextBox)?.SelectAll();
        }

        private static void TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Intercept the mouse click so it doesn't unselect the text,
            // but ONLY if the TextBox didn't already have focus.
            if (sender is TextBox textBox && !textBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                textBox.Focus();
            }
        }

        #endregion
    }
}