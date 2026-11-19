using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Syncfusion.UI.Xaml.Grid;
using BillPro.Helpers;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    // Split-screen sibling of OpeningBalanceEntryView, scoped to just the
    // two reserved stock categories (AccountCategoryRepository
    // .OpeningStockCategoryCode / ClosingStockCategoryCode) instead of the
    // whole Balance Sheet group — Opening Stock heads on the left, Closing
    // Stock heads on the right, each editable via its own OpeningDebit/
    // OpeningCredit exactly like OpeningBalanceEntryView's grid. Reuses
    // that file's BalanceSheetHeadRow (same namespace, BillPro.Views) rather
    // than redefining an identical wrapper.
    public partial class StockBalanceEntryView : UserControl
    {
        private readonly AccountHeadRepository _acHeadRepo;
        private readonly OpeningStockRepository _openingStockRepo;
        private readonly StockMasterRepository _stockRepo;

        private readonly ObservableCollection<BalanceSheetHeadRow> _openingRows = new();
        private readonly ObservableCollection<BalanceSheetHeadRow> _closingRows = new();

        public StockBalanceEntryView()
        {
            InitializeComponent();

            _acHeadRepo = new AccountHeadRepository();

            // Both needed only for the reference-value band at the bottom
            // (Opening Stock Report / Current Stock Report grand totals) —
            // see LoadReferenceValues.
            _openingStockRepo = new OpeningStockRepository();
            _openingStockRepo.InitializeDatabase();
            _stockRepo = new StockMasterRepository();
            _stockRepo.InitializeDatabase();

            dgOpeningHeads.ItemsSource = _openingRows;
            dgClosingHeads.ItemsSource = _closingRows;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            LoadHeads();
            LoadReferenceValues();
            RecalculateEnteredTotals();
        }

        // ── Data loading ────────────────────────────────────────────

        private void LoadHeads()
        {
            LoadInto(_openingRows, AccountCategoryRepository.OpeningStockCategoryCode);
            LoadInto(_closingRows, AccountCategoryRepository.ClosingStockCategoryCode);
        }

        private void LoadInto(ObservableCollection<BalanceSheetHeadRow> target, int categoryCode)
        {
            foreach (var row in target)
                row.PropertyChanged -= Row_PropertyChanged;
            target.Clear();

            foreach (var head in _acHeadRepo.GetHeadsByCategory(categoryCode))
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
                target.Add(row);
            }
        }

        // Reference-only figures pulled from the two existing stock
        // reports — Opening Stock Report's own grand total
        // (OpeningStockRepository.GetOpeningStockReport, same
        // TotalQty * PurchaseRate sum its own footer shows) and Current
        // Stock Report's own grand total (StockMasterRepository
        // .GetCurrentStockReport as of today, same CurrentQty *
        // PurchaseRate sum CurrentStockReportView's footer shows). This is
        // what the physical stock actually values at per inventory — NOT
        // wired into Save or any accounting calculation, purely so whoever
        // is punching in the heads above (e.g. splitting one figure across
        // "Opening Stock 18%", "Opening Stock 12%") knows what total
        // they're aiming to reconcile the Entered Total band against.
        private void LoadReferenceValues()
        {
            decimal openingReportValue = _openingStockRepo.GetOpeningStockReport().Sum(r => r.Amount);
            double closingReportValue = _stockRepo.GetCurrentStockReport(DateTime.Today).Sum(r => r.Amount);

            lblOpeningReportValue.Text = openingReportValue.ToString("N2", CultureInfo.InvariantCulture);
            lblClosingReportValue.Text = closingReportValue.ToString("N2", CultureInfo.InvariantCulture);
        }

        // ── Totals ────────────────────────────────────────────────────

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BalanceSheetHeadRow.OpeningCredit)
                || e.PropertyName == nameof(BalanceSheetHeadRow.OpeningDebit))
            {
                RecalculateEnteredTotals();
            }
        }

        private void DgHeads_CurrentCellEndEdit(object sender, CurrentCellEndEditEventArgs e)
        {
            // Belt-and-braces — bound TwoWay properties already trigger
            // Row_PropertyChanged, but this covers edits that commit
            // without a distinct PropertyChanged raise timing. Shared by
            // both grids (same handler name wired to each in the XAML).
            RecalculateEnteredTotals();
        }

        private void RecalculateEnteredTotals()
        {
            // Opening Stock is entered via its Debit column only (left
            // grid), Closing Stock via its Credit column only (right
            // grid) — see the XAML. So Opening's total reads Debit -
            // Credit as before, but Closing's must read Credit - Debit or
            // a perfectly normal positive closing stock value shows up
            // negative here (and then everywhere downstream that reads
            // these heads — GetTradingProfitLoss, GetBalanceSheet — since
            // they all trust this same OpeningCredit field). Same
            // convention as TaxLedgerRepository.GetStockHeadValue.
            decimal openingTotal = _openingRows.Sum(r => r.OpeningDebit - r.OpeningCredit);
            decimal closingTotal = _closingRows.Sum(r => r.OpeningCredit - r.OpeningDebit);

            lblOpeningEnteredTotal.Text = openingTotal.ToString("N2", CultureInfo.InvariantCulture);
            lblClosingEnteredTotal.Text = closingTotal.ToString("N2", CultureInfo.InvariantCulture);
        }

        // ── Save ──────────────────────────────────────────────────────

        private void BtnSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (!BillPro.Helpers.PermissionGate.RequireEdit()) return;
            dgOpeningHeads.SelectionController.CurrentCellManager?.EndEdit();
            dgClosingHeads.SelectionController.CurrentCellManager?.EndEdit();

            try
            {
                var updates = _openingRows.Concat(_closingRows).Select(r => new AccountHeadMaster
                {
                    Id = r.Id,
                    AcHeadCode = r.AcHeadCode,
                    OpeningCredit = r.OpeningCredit,
                    OpeningDebit = r.OpeningDebit
                }).ToList();

                _acHeadRepo.SaveOpeningBalances(updates);

                MessageBox.Show("Opening/Closing Stock balances saved successfully.", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                LoadHeads();
                RecalculateEnteredTotals();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving balances:\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}