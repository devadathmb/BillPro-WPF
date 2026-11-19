using Syncfusion.UI.Xaml.Grid;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BillPro.Models;
using BillPro.Repositories;
// Alias to resolve ambiguity with Syncfusion.UI.Xaml.Grid.SelectionChangedEventArgs,
// same convention used in the other entry views.
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace BillPro.Views
{
    public partial class SalesSeriesConfigView : UserControl
    {
        private readonly SalesSeriesRepository _repo = new();

        // Null while adding a brand-new series; set to the prefix being
        // edited once a grid row is selected. Prefix is the primary key and
        // is locked (read-only) whenever this is non-null.
        private string _editingPrefix = null;

        public SalesSeriesConfigView()
        {
            InitializeComponent();
            _repo.InitializeDatabase();

            LoadGrid();
            ResetFormForNew();

            // Corrected from ToggleFields(false);
            ToggleFields(false);
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => btnNewSeries.Focus()));
        }

        // ════════════════════════════════════════════════════════════════════
        // FORM STATE MANAGEMENT
        // ════════════════════════════════════════════════════════════════════
        private void ToggleFields(bool isEnabled)
        {
            txtPrefix.IsEnabled = isEnabled;
            txtStartNumber.IsEnabled = isEnabled;
            cmbCustomerType.IsEnabled = isEnabled;
            cmbTransactionType.IsEnabled = isEnabled;
            cmbSuffixType.IsEnabled = isEnabled;
            cmbFySuffixFormat.IsEnabled = isEnabled;
            txtCustomSuffix.IsEnabled = isEnabled;
            chkIsActive.IsEnabled = isEnabled;

            btnSaveSeries.IsEnabled = isEnabled;
        }

        // ════════════════════════════════════════════════════════════════════
        // GRID
        // ════════════════════════════════════════════════════════════════════
        private void LoadGrid()
        {
            sfSeries.ItemsSource = _repo.GetAll();
        }

        private void SfSeries_SelectionChanged(object sender, GridSelectionChangedEventArgs e)
        {
            if (sfSeries.SelectedItem is SalesSeriesMaster series)
                LoadSeriesForEdit(series);
        }

        // Radio behaviour for the grid's "Default" column — checking one row
        // clears every other row's flag in the database, then reloads the
        // grid so the RadioButtons reflect the new single source of truth.
        // Sales Entry reads this via SalesSeriesRepository.GetDefault() to
        // pre-select the series combo on a new bill.
        //
        // IMPORTANT: this is wired to Click, not Checked. RadioButton raises
        // Checked whenever IsChecked becomes true — including when the data
        // binding sets it after LoadGrid() rebuilds the rows — so Checked
        // would re-fire this handler on every reload and spin forever
        // (SetDefault -> LoadGrid -> binding sets IsChecked -> Checked fires
        // -> SetDefault -> ...), freezing the UI thread. Click only fires
        // from actual user interaction, so it can't be re-triggered by the
        // binding. _isApplyingDefault is kept as a belt-and-braces guard in
        // case anything else ever routes through here programmatically.
        private bool _isApplyingDefault = false;

        private void RadioDefault_Click(object sender, RoutedEventArgs e)
        {
            if (_isApplyingDefault) return;
            if (sender is not RadioButton rb || rb.Tag is not SalesSeriesMaster series) return;

            try
            {
                _isApplyingDefault = true;
                _repo.SetDefault(series.Prefix);
                LoadGrid();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error setting default series:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isApplyingDefault = false;
            }
        }

        private void LoadSeriesForEdit(SalesSeriesMaster series)
        {
            _editingPrefix = series.Prefix;

            // Enable form, then lock specific PK/immutable fields
            ToggleFields(true);

            txtPrefix.Text = series.Prefix;
            txtPrefix.IsEnabled = false;                 // PK — locked on edit

            txtStartNumber.Text = series.StartNumber.ToString();
            txtStartNumber.IsEnabled = false;             // locked forever once created

            SelectComboByContent(cmbCustomerType, series.CustomerType);
            SelectComboByContent(cmbTransactionType, series.TransactionType);
            SelectComboByContent(cmbSuffixType, series.SuffixType);

            ApplySuffixTypeVisibility(series.SuffixType);
            if (series.SuffixType == "FinancialYear")
                SelectComboByContent(cmbFySuffixFormat, series.SuffixValue);
            else if (series.SuffixType == "Custom")
                txtCustomSuffix.Text = series.SuffixValue;

            chkIsActive.IsChecked = series.IsActive == 1;

            UpdatePreview();
            btnSaveSeries.Content = "_Update Series";
        }

        // ════════════════════════════════════════════════════════════════════
        // NEW / RESET
        // ════════════════════════════════════════════════════════════════════
        private void BtnNewSeries_Click(object sender, RoutedEventArgs e) => ResetFormForNew();

        private void ResetFormForNew()
        {
            _editingPrefix = null;

            ToggleFields(true);

            txtPrefix.Text = "";
            txtStartNumber.Text = "1";

            cmbCustomerType.SelectedIndex = -1;
            cmbTransactionType.SelectedIndex = -1;
            cmbSuffixType.SelectedIndex = 0;   // "None"
            ApplySuffixTypeVisibility("None");
            cmbFySuffixFormat.SelectedIndex = -1;
            txtCustomSuffix.Text = "";

            chkIsActive.IsChecked = true;

            lblPreview.Text = "";
            btnSaveSeries.Content = "_Save Series";

            sfSeries.SelectedItem = null;
            txtPrefix.Focus();
        }

        // ════════════════════════════════════════════════════════════════════
        // SUFFIX TYPE TOGGLE
        // ════════════════════════════════════════════════════════════════════
        private void CmbSuffixType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string suffixType = (cmbSuffixType.SelectedItem as ComboBoxItem)?.Content as string ?? "None";
            ApplySuffixTypeVisibility(suffixType);
            UpdatePreview();
        }

        private void ApplySuffixTypeVisibility(string suffixType)
        {
            // Guard against initialization events firing before the UI is fully built
            if (pnlFySuffixFormat == null || pnlCustomSuffix == null) return;

            pnlFySuffixFormat.Visibility = suffixType == "FinancialYear" ? Visibility.Visible : Visibility.Collapsed;
            pnlCustomSuffix.Visibility = suffixType == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePreview()
        {
            // Guard against initialization events
            if (txtPrefix == null || txtStartNumber == null || lblPreview == null) return;

            try
            {
                string prefix = txtPrefix.Text.Trim();
                if (string.IsNullOrWhiteSpace(prefix) || !int.TryParse(txtStartNumber.Text, out int startNo))
                {
                    lblPreview.Text = "";
                    return;
                }

                string suffixType = (cmbSuffixType.SelectedItem as ComboBoxItem)?.Content as string ?? "None";
                string suffixValue = suffixType switch
                {
                    "FinancialYear" => (cmbFySuffixFormat.SelectedItem as ComboBoxItem)?.Content as string,
                    "Custom" => txtCustomSuffix.Text.Trim(),
                    _ => null
                };

                var previewSeries = new SalesSeriesMaster
                {
                    Prefix = prefix,
                    StartNumber = startNo,
                    CurrentNumber = 0,
                    SuffixType = suffixType,
                    SuffixValue = suffixValue
                };

                lblPreview.Text = $"Preview: {FormatPreviewBillNo(previewSeries)}";
            }
            catch
            {
                lblPreview.Text = "";
            }
        }

        private static string FormatPreviewBillNo(SalesSeriesMaster series)
        {
            string suffix = series.SuffixType switch
            {
                "FinancialYear" => "-" + GetFinancialYearLabel(series.SuffixValue),
                "Custom" => "-" + (series.SuffixValue ?? ""),
                _ => ""
            };
            return $"{series.Prefix}-{series.StartNumber}{suffix}";
        }

        private static string GetFinancialYearLabel(string format)
        {
            int year = DateTime.Now.Year;
            int nextYear = year + 1;
            return format switch
            {
                "YY-YY" => $"{year % 100:D2}-{nextYear % 100:D2}",
                "YYYY-YYYY" => $"{year}-{nextYear}",
                "YYYY-YY" => $"{year}-{nextYear % 100:D2}",
                _ => $"{year}-{nextYear % 100:D2}"
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // SAVE
        // ════════════════════════════════════════════════════════════════════
        private void BtnSaveSeries_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateForm()) return;

            string suffixType = (cmbSuffixType.SelectedItem as ComboBoxItem)?.Content as string ?? "None";
            string suffixValue = suffixType switch
            {
                "FinancialYear" => (cmbFySuffixFormat.SelectedItem as ComboBoxItem)?.Content as string,
                "Custom" => txtCustomSuffix.Text.Trim(),
                _ => null
            };

            var series = new SalesSeriesMaster
            {
                Prefix = txtPrefix.Text.Trim(),
                StartNumber = int.Parse(txtStartNumber.Text.Trim()),
                CustomerType = (cmbCustomerType.SelectedItem as ComboBoxItem)?.Content as string,
                TransactionType = (cmbTransactionType.SelectedItem as ComboBoxItem)?.Content as string,
                SuffixType = suffixType,
                SuffixValue = suffixValue,
                IsActive = chkIsActive.IsChecked == true ? 1 : 0
            };

            try
            {
                if (_editingPrefix == null)
                {
                    if (_repo.PrefixExists(series.Prefix))
                    {
                        MessageBox.Show($"Prefix \"{series.Prefix}\" is already registered.",
                            "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                        txtPrefix.Focus();
                        return;
                    }

                    _repo.AddSeries(series);
                    MessageBox.Show("Series registered successfully.", "Saved",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    _repo.UpdateSeries(series);
                    MessageBox.Show("Series updated successfully.", "Updated",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                LoadGrid();

                // Disable fields post-save rather than wiping the form
                ToggleFields(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving series:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ValidateForm()
        {
            if (string.IsNullOrWhiteSpace(txtPrefix.Text))
            {
                MessageBox.Show("Please enter a prefix.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtPrefix.Focus();
                return false;
            }

            if (!int.TryParse(txtStartNumber.Text, out int startNo) || startNo < 1)
            {
                MessageBox.Show("Please enter a valid starting number (1 or greater).",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                txtStartNumber.Focus();
                return false;
            }

            if (cmbCustomerType.SelectedItem == null)
            {
                MessageBox.Show("Please select a customer type.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbCustomerType.Focus();
                return false;
            }

            if (cmbTransactionType.SelectedItem == null)
            {
                MessageBox.Show("Please select a transaction type.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbTransactionType.Focus();
                return false;
            }

            string suffixType = (cmbSuffixType.SelectedItem as ComboBoxItem)?.Content as string ?? "None";
            if (suffixType == "FinancialYear" && cmbFySuffixFormat.SelectedItem == null)
            {
                MessageBox.Show("Please select a Financial Year format.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                cmbFySuffixFormat.Focus();
                return false;
            }

            if (suffixType == "Custom" && string.IsNullOrWhiteSpace(txtCustomSuffix.Text))
            {
                MessageBox.Show("Please enter a custom suffix.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtCustomSuffix.Focus();
                return false;
            }

            if (_editingPrefix != null && _repo.IsSeriesInUse(_editingPrefix))
            {
                // Informational only; safe to allow through based on repository checks.
            }

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════
        private static void SelectComboByContent(ComboBox combo, string content)
        {
            if (string.IsNullOrEmpty(content)) { combo.SelectedIndex = -1; return; }

            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content as string, content, StringComparison.OrdinalIgnoreCase));
        }
    }
}