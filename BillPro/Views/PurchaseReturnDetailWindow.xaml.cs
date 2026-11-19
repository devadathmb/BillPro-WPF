using System.Collections.Generic;
using System.Windows;
using BillPro.Models;

namespace BillPro.Views
{
    /// <summary>
    /// Read-only "drill-down" popup for a single purchase return entry,
    /// opened from either Purchase Return report screen's right-click "View
    /// Details" menu item. Mirrors PurchaseDetailWindow/SalesReturnDetailWindow
    /// — just displays what's already saved, no editing, no repository calls
    /// of its own.
    /// </summary>
    public partial class PurchaseReturnDetailWindow : Window
    {
        public PurchaseReturnDetailWindow(PurchaseReturnSummary summary, List<PurchaseReturnItemDetail> items)
        {
            InitializeComponent();

            txtEntryNo.Text = summary.FormattedEntryNo;
            txtEntryDate.Text = summary.EntryDate.ToString("dd-MMM-yyyy");
            txtVendorName.Text = summary.VendorName;
            txtGstNo.Text = summary.VendorGstNo;
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