using System.Collections.Generic;
using System.Linq;

namespace BillPro.Helpers
{
    public class NavCatalogNode
    {
        public string Title { get; set; }
        public bool IsLeaf { get; set; }
        public List<NavCatalogNode> Children { get; set; } = new();
    }

    /// <summary>
    /// A data-only mirror of MainWindow.BuildNavTree's section/leaf structure
    /// — same titles, no factories. Used solely by MenuCustomizationView to
    /// render the checkbox tree. IMPORTANT: if you add/rename/remove a Leaf(...)
    /// in MainWindow.BuildNavTree, mirror that change here too, or menu
    /// customization will show a stale form list. Titles here must match
    /// MainWindow's Leaf(title, ...) title argument exactly (not the
    /// hotkey-suffixed display text) since that raw title is what
    /// UserSession.CanSeeForm(formKey) checks against.
    /// </summary>
    public static class NavCatalog
    {
        public static List<NavCatalogNode> BuildTree() => new()
        {
            Section("MASTER FILES",
                Leaf("Account Category Registration"),
                Leaf("Account Master Registration"),
                Leaf("Item Registration"),
                Leaf("Item Category Registration"),
                Leaf("Company Registration"),
                Leaf("HSN Code Registration"),
                Leaf("Vehicle Registration"),
                Leaf("Vendor/Customer Registration"),
                Leaf("Product Formula Registration")),

            Section("ENTRIES",
                Leaf("Production Entry"),
                Leaf("Purchase Entry"),
                Leaf("Sales Entry"),
                Leaf("Opening Stock Entry"),
                Leaf("Purchase Return Entry"),
                Leaf("Sales Return Entry"),
                Leaf("Excess Stock Entry"),
                Leaf("Short Stock Entry")),

            Section("REPORTS",
                Group("Purchase Reports",
                    Leaf("Purchase Report"),
                    Leaf("Item-wise Purchase Report"),
                    Leaf("Purchase Settlement Report"),
                    Leaf("Purchase Return Report"),
                    Leaf("Item-wise Purchase Return Report"),
                    Leaf("Purchase Return Settlement Report")),
                Group("Sales Reports",
                    Leaf("Sales Report"),
                    Leaf("Item-wise Sales Report"),
                    Leaf("Sales Settlement Report"),
                    Leaf("Sales Return Report"),
                    Leaf("Item-wise Sales Return Report"),
                    Leaf("Sales Return Settlement Report")),
                Group("Stock Reports",
                    Leaf("Individual Stock Report"),
                    Leaf("Current Stock Report"),
                    Leaf("Opening Stock Report")),
                Leaf("Excess Itemwise Report"),
                Leaf("Short Itemwise Report")),

            Section("ACCOUNTS",
                Leaf("Daybook"),
                Leaf("CashBook"),
                Leaf("Ledger (Individual Heads)"),
                Leaf("Trial Balance"),
                Leaf("Balance Sheet"),
                Leaf("Trading Profit & Loss"),
                Leaf("Sundry Debtors"),
                Leaf("Sundry Creditors"),
                Leaf("Journal Entry"),
                Leaf("Receipt Entry"),
                Leaf("Payment Entry"),
                Leaf("Opening Balance Entry"),
                Leaf("Opening/Closing Value Entry")),

            Section("SYSTEM",
                Leaf("Sales Series Config"),
                Leaf("Resync to Accounts"),
                Leaf("Set Opening Cash In Hand")),

            // The three new admin screens themselves are deliberately NOT
            // included here — they're gated by role (Admin/Developer only)
            // in MainWindow, not by per-user menu denial, so there's no
            // reason for them to appear in a Normal user's own checkbox tree.
        };

        private static NavCatalogNode Section(string title, params NavCatalogNode[] children) =>
            new() { Title = title, IsLeaf = false, Children = children.ToList() };

        private static NavCatalogNode Group(string title, params NavCatalogNode[] children) =>
            new() { Title = title, IsLeaf = false, Children = children.ToList() };

        private static NavCatalogNode Leaf(string title) =>
            new() { Title = title, IsLeaf = true };
    }
}