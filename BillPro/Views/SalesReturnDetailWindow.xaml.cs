using System.Collections.Generic;
using System.Windows;
using BillPro.Models;

namespace BillPro.Views
{
    /// <summary>
    /// Read-only "drill-down" popup for a single sales return entry, opened
    /// from either Sales Return report screen's right-click "View Details"
    /// menu item. Mirrors SalesDetailWindow — just displays what's already
    /// saved, no editing, no repository calls of its own.
    /// </summary>
    public partial class SalesReturnDetailWindow : Window
    {
        public SalesReturnDetailWindow(SalesReturnSummary summary, List<SalesReturnItemDetail> items)
        {
            InitializeComponent();

            txtEntryNo.Text = summary.FormattedEntryNo;
            txtEntryDate.Text = summary.EntryDate.ToString("dd-MMM-yyyy");
            txtCustomerName.Text = string.IsNullOrWhiteSpace(summary.CustomerName) ? "Walk-in" : summary.CustomerName;
            txtGstNo.Text = summary.CustomerGstNo;
            txtOriginalBillNo.Text = summary.OriginalBillNo;
            txtBillType.Text = summary.BillType;

            gridItems.ItemsSource = items;

            txtNetTotal.Text = summary.NetTotal.ToString("N2");
            txtCash.Text = summary.ToCashAmount.ToString("N2");
            txtBank.Text = summary.ToBankAmount.ToString("N2");
            txtBankHead.Text = string.IsNullOrWhiteSpace(summary.BankHeadName) ? "—" : summary.BankHeadName;
            txtCredit.Text = summary.ToCreditAmount.ToString("N2");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}