using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace BillPro.Views
{
    /// <summary>
    /// Generic "pick one of these" popup shown when a Daybook/Ledger Report
    /// row is a grouped Sales/SalesReturn bucket (Narration reads
    /// "billFrom - billTo" rather than a single voucher) — double-clicking
    /// that row can't jump straight to one entry, so this lists the actual
    /// bills/entries behind it first. Double-clicking a row here sets
    /// <see cref="SelectedKey"/> and closes with DialogResult = true; the
    /// caller (EntryDrillDownHelper) reads it back and does the actual
    /// EntryEditNavigator.RequestEdit call, since the key shape differs by
    /// module ((Prefix, BillNo) for Sales, int EntryNo for SalesReturn —
    /// same TKey-per-channel convention as EntryEditNavigator itself).
    /// </summary>
    public partial class BillRangePickerWindow : Window
    {
        public class Row
        {
            public string FormattedNo { get; set; }
            public string DateLabel { get; set; }
            public string PartyName { get; set; }
            public double Amount { get; set; }
            public object Key { get; set; }
        }

        public object SelectedKey { get; private set; }

        public BillRangePickerWindow(string title, List<Row> rows)
        {
            InitializeComponent();

            Title = title;
            lblTitle.Text = title;
            gridBills.ItemsSource = rows;
        }

        private void GridBills_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            TrySelect();
        }

        private void TrySelect()
        {
            if (gridBills.SelectedItem is not Row row) return;

            SelectedKey = row.Key;
            DialogResult = true;
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}