using System.Collections.Generic;
using System.Windows;
using BillPro.Models;

namespace BillPro.Views
{
    /// <summary>
    /// Read-only "drill-down" popup for a single purchase bill, opened from
    /// PurchaseReportView's right-click "View Details" menu item. Just
    /// displays what's already saved — no editing, no repository calls of
    /// its own (the caller fetches summary/items/expenses and passes them in).
    /// </summary>
    public partial class PurchaseDetailWindow : Window
    {
        public PurchaseDetailWindow(PurchaseSummary summary, List<PurchaseItemDetail> items,
            List<PurchaseExternalExpense> expenses)
        {
            InitializeComponent();

            txtBillNo.Text = summary.BillNo;
            txtBillDate.Text = summary.BillDate.ToString("dd-MMM-yyyy");
            txtVendorName.Text = summary.VendorName;
            txtGstNo.Text = summary.GstNo;
            txtPurchaseNo.Text = summary.PurchaseNo.ToString();
            txtPurchaseDate.Text = summary.PurchaseDate.ToString("dd-MMM-yyyy");

            gridItems.ItemsSource = items;
            gridExpenses.ItemsSource = expenses;

            txtNetTotal.Text = summary.NetTotal.ToString("N2");
            txtCash.Text = summary.ToCashAmount.ToString("N2");
            txtBank.Text = summary.ToCardAmount.ToString("N2");
            txtBankHead.Text = string.IsNullOrWhiteSpace(summary.BankHeadName) ? "—" : summary.BankHeadName;
            txtCredit.Text = summary.ToCreditAmount.ToString("N2");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}