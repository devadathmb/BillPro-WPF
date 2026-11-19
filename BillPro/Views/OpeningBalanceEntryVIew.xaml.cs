using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    /// <summary>
    /// Editable row wrapper around AccountHeadMaster for the grid —
    /// raises PropertyChanged so OpeningCredit/OpeningDebit edits
    /// made in the SfDataGrid immediately update the totals band.
    /// </summary>
    public class BalanceSheetHeadRow : ObservableObject
    {
        public int Id { get; set; }
        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }

        private decimal _openingCredit;
        public decimal OpeningCredit
        {
            get => _openingCredit;
            set => SetProperty(ref _openingCredit, value);
        }

        private decimal _openingDebit;
        public decimal OpeningDebit
        {
            get => _openingDebit;
            set => SetProperty(ref _openingDebit, value);
        }
    }

    // Read-only row shape used only for Print/PDF/Excel export — mirrors
    // BalanceSheetHeadRow's four display columns but adds the IReportRow
    // hooks (bold/shaded Total row, blanked-out Code/Name on the Opening
    // Cash line) that the grid-editing row type has no reason to carry.
    public class OpeningBalanceExportRow : IReportRow
    {
        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }
        public decimal OpeningDebit { get; set; }
        public decimal OpeningCredit { get; set; }
        public bool IsTotalRow { get; set; }

        public bool IsReportHighlighted => IsTotalRow;
        public bool IsReportFieldBlank(string propertyName) => false;
        public int GetMergedLabelSpan(out string mergedLabel) { mergedLabel = null; return 0; }
    }

    public partial class OpeningBalanceEntryView : UserControl
    {
        private readonly AccountHeadRepository _acHeadRepo;
        private readonly TaxLedgerRepository _taxLedgerRepo;

        private readonly ObservableCollection<BalanceSheetHeadRow> _allRows = new();

        public OpeningBalanceEntryView()
        {
            InitializeComponent();

            _acHeadRepo = new AccountHeadRepository();
            _taxLedgerRepo = new TaxLedgerRepository();

            dgHeads.ItemsSource = _allRows;
            // NOTE: dgHeads.View is not created synchronously when
            // ItemsSource is set — Syncfusion builds it once the grid's
            // control template applies. Setting the Filter here in the
            // constructor throws a NullReferenceException because View is
            // still null at this point. Assign it in Loaded instead, where
            // the template is guaranteed to have applied.

            Loaded += (s, e) =>
            {
                dgHeads.View.Filter = FilterRow;

                LoadHeads();
                LoadOpeningCash();
                RecalculateTotals();

                // Dispatcher.BeginInvoke: on first load the TextBox isn't
                // always keyboard-focusable yet at the moment Loaded fires
                // (grid/template still settling), so a direct Focus() call
                // here can silently no-op. Queuing it after layout finishes
                // makes it land reliably.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() =>
                    {
                        txtSearch.Focus();
                        System.Windows.Input.Keyboard.Focus(txtSearch);
                    }));
            };
        }

        // ── Data loading ────────────────────────────────────────────

        private void LoadHeads()
        {
            _allRows.CollectionChanged -= AllRows_CollectionChanged;

            foreach (var row in _allRows)
                row.PropertyChanged -= Row_PropertyChanged;

            _allRows.Clear();

            // Balance Sheet heads (AcGroupId == 0, matching the "Balance
            // Sheet" entry in AccountHeadRegistration's AcGroup combo)
            // PLUS every head registered under the reserved Opening Stock
            // category (AccountCategoryRepository.OpeningStockCategoryCode)
            // — regardless of whatever AcGroupId it was given — so users
            // can punch in "Opening Stock 18%", "Opening Stock 12%" etc.
            // right here alongside Cash/Bank/other Balance Sheet heads,
            // in the same grid, using the exact same OpeningDebit field.
            // GetTradingProfitLoss then picks these up by category for the
            // Trading A/c, and this screen's own totals band already sums
            // every row in _allRows together, so no separate "stock total"
            // display is needed — they're just rows like any other.
            var headsToShow = _acHeadRepo.GetBalanceSheetHeads()
                .Concat(_acHeadRepo.GetHeadsByCategory(AccountCategoryRepository.OpeningStockCategoryCode));

            foreach (var head in headsToShow)
            {
                var row = new BalanceSheetHeadRow
                {
                    Id = head.Id,
                    AcHeadCode = head.AcHeadCode,
                    AcHeadName = head.AcHeadName,
                    OpeningCredit = head.OpeningCredit,
                    OpeningDebit = head.OpeningDebit
                };
                row.PropertyChanged += Row_PropertyChanged;
                _allRows.Add(row);
            }

            _allRows.CollectionChanged += AllRows_CollectionChanged;
        }

        private void LoadOpeningCash()
        {
            var opening = _taxLedgerRepo.GetOpeningBalance();
            txtOpeningCash.Text = opening.Amount.ToString(CultureInfo.InvariantCulture);
        }

        // ── Search filter ────────────────────────────────────────────

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            dgHeads.View.RefreshFilter();
        }

        private bool FilterRow(object obj)
        {
            if (obj is not BalanceSheetHeadRow row) return true;
            string term = txtSearch.Text?.Trim();
            if (string.IsNullOrEmpty(term)) return true;

            return (row.AcHeadCode?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                || (row.AcHeadName?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ── Totals ────────────────────────────────────────────────────

        private void AllRows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RecalculateTotals();
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BalanceSheetHeadRow.OpeningCredit)
                || e.PropertyName == nameof(BalanceSheetHeadRow.OpeningDebit))
            {
                RecalculateTotals();
            }
        }

        private void DgHeads_CurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
        {
            // Belt-and-braces — bound TwoWay properties already trigger
            // Row_PropertyChanged, but this covers edits that commit
            // without a distinct PropertyChanged raise timing.
            RecalculateTotals();
        }

        private void TxtOpeningCash_TextChanged(object sender, TextChangedEventArgs e)
        {
            RecalculateTotals();
        }

        // Opening Cash In Hand isn't a row in the grid, but it still has to
        // be folded into the credit side or the trial balance can never tie
        // out — the grid-only totals were leaving it out entirely.
        private decimal GetOpeningCashAmount()
        {
            decimal.TryParse(txtOpeningCash.Text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out decimal openingCash);
            return openingCash;
        }

        private void RecalculateTotals()
        {
            // Opening Stock heads are already rows in _allRows (see
            // LoadHeads), so they're already included in this Sum — no
            // separate stock figure to add on top of it.
            decimal totalDebit = _allRows.Sum(r => r.OpeningDebit) + GetOpeningCashAmount();
            decimal totalCredit = _allRows.Sum(r => r.OpeningCredit);
            decimal difference = totalDebit - totalCredit;

            lblTotalDebit.Text = totalDebit.ToString("N2", CultureInfo.InvariantCulture);
            lblTotalCredit.Text = totalCredit.ToString("N2", CultureInfo.InvariantCulture);
            lblDifference.Text = difference.ToString("N2", CultureInfo.InvariantCulture);

            lblDifference.Foreground = difference == 0
                ? System.Windows.Media.Brushes.SeaGreen
                : System.Windows.Media.Brushes.Firebrick;
        }

        // ── Print / PDF / Excel ─────────────────────────────────────────
        private const string ReportTitle = "BALANCE SHEET — OPENING BALANCES";

        private static List<ReportColumn> BuildReportColumns() => new List<ReportColumn>
        {
            new ReportColumn("Code",           nameof(OpeningBalanceExportRow.AcHeadCode), width: 90),
            new ReportColumn("A/c Head Name",  nameof(OpeningBalanceExportRow.AcHeadName), width: 280),
            new ReportColumn("Opening Debit",  nameof(OpeningBalanceExportRow.OpeningDebit),  width: 130, format: "N2", alignment: TextAlignment.Right),
            new ReportColumn("Opening Credit", nameof(OpeningBalanceExportRow.OpeningCredit), width: 130, format: "N2", alignment: TextAlignment.Right),
        };

        // Same rows the grid shows, plus the Opening Cash In Hand figure
        // (not itself a grid row — see GetOpeningCashAmount) and a Total
        // row, so the export always matches exactly what the totals band
        // on screen is showing.
        private List<OpeningBalanceExportRow> BuildExportRows()
        {
            var exportRows = _allRows
                .Select(r => new OpeningBalanceExportRow
                {
                    AcHeadCode = r.AcHeadCode,
                    AcHeadName = r.AcHeadName,
                    OpeningDebit = r.OpeningDebit,
                    OpeningCredit = r.OpeningCredit
                })
                .ToList();

            decimal openingCash = GetOpeningCashAmount();
            exportRows.Add(new OpeningBalanceExportRow
            {
                AcHeadCode = "",
                AcHeadName = "Opening Cash In Hand",
                OpeningDebit = openingCash,
                OpeningCredit = 0
            });

            decimal totalDebit = exportRows.Sum(r => r.OpeningDebit);
            decimal totalCredit = exportRows.Sum(r => r.OpeningCredit);
            exportRows.Add(new OpeningBalanceExportRow
            {
                AcHeadCode = "",
                AcHeadName = "TOTAL",
                OpeningDebit = totalDebit,
                OpeningCredit = totalCredit,
                IsTotalRow = true
            });

            return exportRows;
        }

        private string[] BuildFooterLines(List<OpeningBalanceExportRow> exportRows)
        {
            var totalRow = exportRows.Last();
            decimal difference = totalRow.OpeningDebit - totalRow.OpeningCredit;

            var lines = new List<string>
            {
                $"Total Debit: {totalRow.OpeningDebit:N2}",
                $"Total Credit: {totalRow.OpeningCredit:N2}"
            };

            // Only included when there IS a difference — an already-tied-out
            // opening balance sheet shows no Difference line at all, same
            // convention as the Trading & P&L / Balance Sheet reports.
            if (difference != 0)
                lines.Add($"Difference: {difference:N2}");

            return lines.ToArray();
        }

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            var exportRows = BuildExportRows();
            ReportPrintHelper.Print(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: exportRows,
                footerLines: BuildFooterLines(exportRows),
                subTitle: $"As at {DateTime.Today:dd-MMM-yyyy}",
                pageMargin: new Thickness(40, 60, 28, 28),
                baseFontSize: 11.0);
        }

        private void BtnPdf_Click(object sender, RoutedEventArgs e)
        {
            var exportRows = BuildExportRows();
            ReportPdfHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: exportRows,
                footerLines: BuildFooterLines(exportRows),
                subTitle: $"As at {DateTime.Today:dd-MMM-yyyy}");
        }

        private void BtnExcel_Click(object sender, RoutedEventArgs e)
        {
            var exportRows = BuildExportRows();
            ReportExcelHelper.Export(
                title: ReportTitle,
                columns: BuildReportColumns(),
                rows: exportRows,
                footerLines: BuildFooterLines(exportRows),
                subTitle: $"As at {DateTime.Today:dd-MMM-yyyy}");
        }

        // ── Save ──────────────────────────────────────────────────────

        private void BtnSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireEdit()) return;
            dgHeads.SelectionController.CurrentCellManager?.EndEdit();

            if (!decimal.TryParse(txtOpeningCash.Text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out decimal openingCash))
            {
                MessageBox.Show("Enter a valid Opening Cash In Hand amount (negative is fine, e.g. -1500).",
                    "Invalid Amount", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtOpeningCash.Focus();
                return;
            }

            // Same convention as RecalculateTotals: Opening Cash In Hand
            // folds into the Debit side, not Credit — it was on the wrong
            // side here, which made this check disagree with the totals
            // band the user is actually looking at (off by 2x the cash
            // amount) and threw a false Balance Mismatch warning even when
            // the on-screen Difference read 0.00.
            decimal totalDebit = _allRows.Sum(r => r.OpeningDebit) + openingCash;
            decimal totalCredit = _allRows.Sum(r => r.OpeningCredit);

            if (totalDebit != totalCredit)
            {
                var proceed = MessageBox.Show(
                    $"Debit and Credit totals don't match (Difference: {(totalDebit - totalCredit):N2}).\n\n" +
                    "Do you still want to save?",
                    "Balance Mismatch", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (proceed != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                var updates = _allRows.Select(r => new AccountHeadMaster
                {
                    Id = r.Id,
                    AcHeadCode = r.AcHeadCode,
                    OpeningCredit = r.OpeningCredit,
                    OpeningDebit = r.OpeningDebit
                }).ToList();

                _acHeadRepo.SaveOpeningBalances(updates);

                string currentUser = Environment.UserName;
                _taxLedgerRepo.SetOpeningBalance((double)openingCash, currentUser);

                MessageBox.Show("Opening balances saved successfully.", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                LoadHeads();
                RecalculateTotals();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving opening balances:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}