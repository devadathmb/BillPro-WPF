using System.Collections.Generic;
using System.Windows;
using BillPro.Models;

namespace BillPro.Views
{
    /// <summary>
    /// Read-only "drill-down" popup for a single sales bill, opened from any
    /// of the three Sales report screens' right-click "View Details" menu
    /// item. Mirrors PurchaseDetailWindow — just displays what's already
    /// saved, no editing, no repository calls of its own.
    /// </summary>
    public partial class SalesDetailWindow : Window
    {
        public SalesDetailWindow(SalesSummary summary, List<SalesItemDetail> items)
        {
            InitializeComponent();

            txtBillNo.Text = summary.FormattedBillNo;
            txtBillDate.Text = summary.BillDate.ToString("dd-MMM-yyyy");
            txtCustomerName.Text = string.IsNullOrWhiteSpace(summary.CustomerName) ? "Walk-in" : summary.CustomerName;
            txtGstNo.Text = summary.CustomerGstNo;
            txtPrefix.Text = summary.Prefix;
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