using System.Windows;
using System.Windows.Controls;

namespace BillPro.Helpers
{
    public static class FormValidator
    {
        // Upgraded to accept 'Control' so it works for TextBox, ComboBox, etc.
        public static bool ValidateRequiredFields(params (Control InputControl, string FieldName)[] fields)
        {
            foreach (var field in fields)
            {
                bool isBlank = false;

                // 1. Check if it's a TextBox and empty
                if (field.InputControl is TextBox txt)
                {
                    isBlank = string.IsNullOrWhiteSpace(txt.Text);
                }
                // 2. Check if it's a ComboBox and nothing is selected
                else if (field.InputControl is ComboBox cmb)
                {
                    isBlank = cmb.SelectedValue == null && cmb.SelectedItem == null;
                }

                // If we found a blank field, show the error and stop
                if (isBlank)
                {
                    MessageBox.Show($"Please specify the {field.FieldName}!",
                        "Information",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    field.InputControl.Focus();
                    return false;
                }
            }
            return true;
        }
    }
}