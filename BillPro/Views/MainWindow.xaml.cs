using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock.Layout;
using BillPro.Controls;
using BillPro.Helpers;
using BillPro.Repositories;
using UserControl = System.Windows.Controls.UserControl;

namespace BillPro.Views
{
    public partial class MainWindow : Window
    {
        private class NavEntry
        {
            public string Title { get; }
            public Func<UserControl> Factory { get; }
            public NavEntry(string title, Func<UserControl> factory) { Title = title; Factory = factory; }
        }
        private readonly Dictionary<(ModifierKeys Modifiers, Key Key), NavEntry> _hotkeys = new();
        private readonly Dictionary<string, NavEntry> _navByTitle = new();
        private CascadingSidebarMenu _navMenu;
        private readonly CompanyRepository _companyRepo = new();

        public static MainWindow Instance { get; private set; }

        private const string CompanyDetailsTitle = "Company Details";

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            txtLoggedInUser.Text = UserSession.Current?.DisplayName ?? "";

            LoadCompanyName();

            _navMenu = new CascadingSidebarMenu(NavPanel);
            _navMenu.Build(BuildNavTree());

            PreviewKeyDown += MainWindow_PreviewKeyDown;
            PreviewMouseDown += (s, e) => _navMenu.CloseAllIfClickedOutside(e.OriginalSource as IInputElement);

            DockManager.DocumentClosed += (s, e) => ShowDashboardIfNoTabsOpen();

            Loaded += (s, e) =>
            {
                ShowDashboard();
                CheckFirstRunCompanyDetails();
            };
        }

        /// <summary>
        /// Pulls the company name from Company Details (same
        /// CompanyRepository/CompanyName field the invoice letterhead and
        /// Login screen read from) into the sidebar header, instead of the
        /// hardcoded "CV. Sumber Makmur Djaya Abadi" placeholder. Falls back
        /// to a generic placeholder if nothing's configured yet. Cosmetic
        /// only — never blocks the window from opening on a DB error.
        /// </summary>
        private void LoadCompanyName()
        {
            try
            {
                _companyRepo.InitializeDatabase();
                var company = _companyRepo.Get();

                txtCompanyName.Text = string.IsNullOrWhiteSpace(company?.CompanyName)
                    ? "Your Company Name"
                    : company.CompanyName.Trim();
            }
            catch
            {
                // Leave the XAML placeholder text in place.
            }
        }

        // ── NAV TREE ────────────────────────────────────────────────────────
        private List<NavNode> BuildNavTree()
        {
            var tree = new List<NavNode>();

            // MASTER FILES
            var master = new NavNode("MASTER FILES", icon: "▤") { IsSection = true };
            master.Add(Leaf("Account Category Registration", () => new AccountCategoryRegistrationView()));
            master.Add(Leaf("Account Master Registration", () => new AccountHeadRegistration(), displayTitle: "Acc Head Registraton"));
            master.Add(Leaf("Item Registration", () => new ItemRegistrationView(), Key.I, ModifierKeys.Alt));
            master.Add(Leaf("Item Category Registration", () => new ItemCategoryRegistrationView()));
            master.Add(Leaf("Company Registration", () => new CompanyRegistrationView()));
            master.Add(Leaf("HSN Code Registration", () => new HSNCodeRegistrationView()));
            master.Add(Leaf("Vehicle Registration", () => new VehicleRegistrationView()));
            master.Add(Leaf("Vendor/Customer Registration", () => new VendorCustomerRegistrationView()));
            master.Add(Leaf("Product Formula Registration", () => new ProductFormulaEntryView()));
            tree.Add(master);

            // ENTRIES
            var entries = new NavNode("ENTRIES", icon: "✎") { IsSection = true };
            entries.Add(Leaf("Production Entry", () => new ProductionEntryView(), Key.F2));
            entries.Add(Leaf("Purchase Entry", () => new PurchaseEntryView(), Key.P, ModifierKeys.Alt));
            entries.Add(Leaf("Sales Entry", () => new SalesEntryView(), Key.S, ModifierKeys.Alt));
            entries.Add(Leaf("Opening Stock Entry", () => new OpeningStockEntryView(), Key.O, ModifierKeys.Alt));
            entries.Add(Leaf("Purchase Return Entry", () => new PurchaseReturnEntryView()));
            entries.Add(Leaf("Sales Return Entry", () => new SalesReturnEntryView()));
            entries.Add(Leaf("Excess Stock Entry", () => new ExcessStockEntryView()));
            entries.Add(Leaf("Short Stock Entry", () => new ShortStockEntryView()));
            tree.Add(entries);

            // REPORTS
            var reports = new NavNode("REPORTS", icon: "▦") { IsSection = true };

            var purchaseReports = reports.Add(new NavNode("Purchase Reports"));
            purchaseReports.Add(Leaf("Purchase Report", () => new PurchaseReportView()));
            purchaseReports.Add(Leaf("Item-wise Purchase Report", () => new PurchaseItemReportView()));
            purchaseReports.Add(Leaf("Purchase Settlement Report", () => new PurchaseSettlementReportView()));
            purchaseReports.Add(Leaf("Purchase Return Report", () => new PurchaseReturnReportView()));
            purchaseReports.Add(Leaf("Item-wise Purchase Return Report", () => new PurchaseReturnItemReportView()));
            purchaseReports.Add(Leaf("Purchase Return Settlement Report", () => new PurchaseReturnSettlementReportView()));

            var salesReports = reports.Add(new NavNode("Sales Reports"));
            salesReports.Add(Leaf("Sales Report", () => new SalesReportView()));
            salesReports.Add(Leaf("Item-wise Sales Report", () => new SalesItemReportView()));
            salesReports.Add(Leaf("Sales Settlement Report", () => new SalesSettlementReportView()));
            salesReports.Add(Leaf("Sales Return Report", () => new SalesReturnReportView()));
            salesReports.Add(Leaf("Item-wise Sales Return Report", () => new SalesReturnItemReportView()));
            salesReports.Add(Leaf("Sales Return Settlement Report", () => new SalesReturnSettlementReportView()));

            var stockReports = reports.Add(new NavNode("Stock Reports"));
            stockReports.Add(Leaf("Individual Stock Report", () => new IndividualStockReportView()));
            stockReports.Add(Leaf("Current Stock Report", () => new CurrentStockReportView(), Key.F5));
            stockReports.Add(Leaf("Opening Stock Report", () => new OpeningStockReportView(), Key.F6));

            reports.Add(Leaf("Excess Itemwise Report", () => new ExcessStockItemReportView()));
            reports.Add(Leaf("Short Itemwise Report", () => new ShortStockItemReportView()));
            tree.Add(reports);

            // ACCOUNTS
            var accounts = new NavNode("ACCOUNTS", icon: "$") { IsSection = true };
            accounts.Add(Leaf("Daybook", () => new DaybookReportView()));
            accounts.Add(Leaf("CashBook", () => new CashbookReportView(), displayTitle: "Cashbook Report"));
            accounts.Add(Leaf("Ledger (Individual Heads)", () => new LedgerReportView()));
            accounts.Add(Leaf("Trial Balance", () => new TrialBalanceReportView()));
            accounts.Add(Leaf("Balance Sheet", () => new BalanceSheetReportView()));
            accounts.Add(Leaf("Trading Profit & Loss", () => new TradingProfitLossReportView()));
            accounts.Add(Leaf("Sundry Debtors", () => new SundryDebtorsReportView()));
            accounts.Add(Leaf("Sundry Creditors", () => new SundryCreditorsReportView()));
            accounts.Add(Leaf("Journal Entry", () => new JournalEntryView()));
            accounts.Add(Leaf("Receipt Entry", () => new ReceiptEntryView()));
            accounts.Add(Leaf("Payment Entry", () => new PaymentEntryView()));
            accounts.Add(Leaf("Opening Balance Entry", () => new OpeningBalanceEntryView()));
            accounts.Add(Leaf("Opening/Closing Value Entry", () => new StockBalanceEntryView()));

            tree.Add(accounts);

            // SYSTEM
            var system = new NavNode("SYSTEM", icon: "⚙") { IsSection = true };
            system.Add(Leaf("Sales Series Config", () => new SalesSeriesConfigView()));
            system.Add(Leaf("Resync to Accounts", () => new ResyncSettingsView()));
            system.Add(Leaf("Set Opening Cash In Hand", () => new OpeningCashInHandView()));

            if (UserSession.Current?.IsAdminOrAbove == true)
            {
                system.Add(Leaf("User Registration", () => new UserRegistrationView()));
                system.Add(Leaf("Menu Customization", () => new MenuCustomizationView()));
                system.Add(Leaf("Change Password", () => new ChangePasswordView()));
            }

            tree.Add(system);

            return tree;
        }

        private NavNode Leaf(string title, Func<UserControl> factory,
            Key hotkeyKey = Key.None, ModifierKeys hotkeyModifiers = ModifierKeys.None,
            string displayTitle = null)
        {
            if (UserSession.Current != null && !UserSession.Current.CanSeeForm(title))
                return null;

            var entry = new NavEntry(title, factory);
            _navByTitle[title] = entry;
            if (hotkeyKey != Key.None)
                _hotkeys[(hotkeyModifiers, hotkeyKey)] = entry;

            string shownAs = displayTitle ?? title;
            string hotkeyLabel = hotkeyModifiers == ModifierKeys.Alt && hotkeyKey != Key.None
                ? $"  (Alt+{hotkeyKey})"
                : (hotkeyModifiers == ModifierKeys.None && (hotkeyKey == Key.F5 || hotkeyKey == Key.F6) ? $"  ({hotkeyKey})" : "");

            return new NavNode(shownAs + hotkeyLabel, () => OpenOrActivate(entry));
        }

        public bool OpenOrActivateTitle(string title)
        {
            if (!_navByTitle.TryGetValue(title, out var entry)) return false;
            OpenOrActivate(entry);
            return true;
        }

        private void OpenOrActivate(NavEntry entry)
        {
            LayoutDocument existing = DockManager.Layout
                .Descendents()
                .OfType<LayoutDocument>()
                .FirstOrDefault(d => d.Title == entry.Title);

            if (existing != null)
            {
                existing.IsActive = true;
                return;
            }

            LayoutDocumentPane targetPane = DockManager.Layout
                .Descendents()
                .OfType<LayoutDocumentPane>()
                .FirstOrDefault() ?? MainDocumentPane;

            LayoutDocument doc = new LayoutDocument
            {
                Title = entry.Title,
                Content = entry.Factory(),
                CanClose = true
            };

            doc.Closed += (_, __) => (doc.Content as IDisposable)?.Dispose();

            targetPane.Children.Add(doc);
            doc.IsActive = true;
        }

        // ── DASHBOARD ───────────────────────────────────────────────────────
        private const string DashboardTitle = "Dashboard";
        private LayoutDocument _dashboardDoc;

        private void BtnDashboardHome_Click(object sender, RoutedEventArgs e) => ShowDashboard();

        private void LogoPanel_Click(object sender, MouseButtonEventArgs e) => ShowDashboard();

        private void ShowDashboard()
        {
            if (_dashboardDoc == null)
            {
                _dashboardDoc = new LayoutDocument
                {
                    Title = DashboardTitle,
                    Content = new DashboardView(),
                    CanClose = false
                };

                LayoutDocumentPane targetPane = DockManager.Layout
                    .Descendents()
                    .OfType<LayoutDocumentPane>()
                    .FirstOrDefault() ?? MainDocumentPane;

                targetPane.Children.Insert(0, _dashboardDoc);
            }

            _dashboardDoc.IsActive = true;
        }

        private void ShowDashboardIfNoTabsOpen()
        {
            bool anyOtherTabsOpen = DockManager.Layout
                .Descendents()
                .OfType<LayoutDocument>()
                .Any(d => d != _dashboardDoc);

            if (!anyOtherTabsOpen) ShowDashboard();
        }

        // ── HOTKEYS ─────────────────────────────────────────────────────────
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys modifiers = Keyboard.Modifiers;

            const ModifierKeys unlockModifiers = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;
            if (modifiers == unlockModifiers && key == Key.C)
            {
                TryUnlockCompanyDetails();
                e.Handled = true;
                return;
            }

            if (_hotkeys.TryGetValue((modifiers, key), out var entry))
            {
                OpenOrActivate(entry);
                e.Handled = true;
            }
        }

        // ── COMPANY DETAILS (first-run + hidden-unlock only) ─────────────────

        private void CheckFirstRunCompanyDetails()
        {
            if (UserSession.Current?.IsDeveloper != true) return;

            var repo = new CompanyRepository();
            repo.InitializeDatabase();

            if (!repo.Get().IsLocked)
            {
                OpenCompanyDetailsTab();
            }
        }

        private void TryUnlockCompanyDetails()
        {
            if (UserSession.Current?.IsDeveloper != true)
            {
                MessageBox.Show("Only the Developer account can access Company Details.",
                    "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OpenCompanyDetailsTab();
        }

        private void OpenCompanyDetailsTab()
        {
            LayoutDocument existing = DockManager.Layout
                .Descendents()
                .OfType<LayoutDocument>()
                .FirstOrDefault(d => d.Title == CompanyDetailsTitle);

            if (existing != null)
            {
                existing.IsActive = true;
                return;
            }

            var view = new CompanyDetailsView();

            LayoutDocument doc = new LayoutDocument
            {
                Title = CompanyDetailsTitle,
                Content = view,
                CanClose = true
            };

            // Company Details can change the name/logo — refresh the
            // sidebar the moment it's saved & locked, so the header doesn't
            // keep showing stale branding until the app is restarted.
            view.SavedAndLocked += (s, e) => { doc.Close(); LoadCompanyName(); };

            LayoutDocumentPane targetPane = DockManager.Layout
                .Descendents()
                .OfType<LayoutDocumentPane>()
                .FirstOrDefault() ?? MainDocumentPane;

            targetPane.Children.Add(doc);
            doc.IsActive = true;
        }

        // ── SIDEBAR TOGGLE ────────────────────────────────────────────────────

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            var sidebarCol = ((Grid)Sidebar.Parent).ColumnDefinitions[0];

            if (sidebarCol.Width.Value > 50)
            {
                sidebarCol.Width = new GridLength(42);
                LogoPanel.Visibility = Visibility.Collapsed;
                NavPanel.Visibility = Visibility.Collapsed;
                btnToggleSidebar.Content = "☰";
            }
            else
            {
                sidebarCol.Width = new GridLength(220);
                LogoPanel.Visibility = Visibility.Visible;
                NavPanel.Visibility = Visibility.Visible;
                btnToggleSidebar.Content = "✕";
            }
        }

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show("Log out of the current account?", "Confirm Logout",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            UserSession.End();

            var login = new LoginWindow();
            Application.Current.MainWindow = login;
            login.Show();
            Close();
        }
    }
}