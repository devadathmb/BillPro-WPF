using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BillPro.Helpers;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class DashboardView : UserControl
    {
        private readonly DashboardRepository _repo = new DashboardRepository();

        // Every Border styled as a glass card (CardStyle) — collected once
        // after InitializeComponent, then used every layout pass to figure
        // out exactly where imgLogoBlurred should be allowed to paint.
        private List<Border> _glassCards = new List<Border>();

        public DashboardView()
        {
            InitializeComponent();
            txtGreeting.Text = Greeting();
            txtToday.Text = DateTime.Today.ToString("dddd, dd MMMM yyyy");

            LoadWatermarkLogo();

            _glassCards = FindVisualChildren<Border>(this)
                .Where(b => b.Style == (Style)Resources["CardStyle"])
                .ToList();

            // LayoutUpdated fires on resize, scroll, content changes — every
            // case where a card's on-screen rect could have moved. Cheap
            // enough here (~10 rects) to just recompute every time rather
            // than trying to enumerate SizeChanged/ScrollChanged separately.
            LayoutUpdated += DashboardView_LayoutUpdated;
            Unloaded += (s, e) => LayoutUpdated -= DashboardView_LayoutUpdated;

            Loaded += (s, e) => LoadData();
        }

        private void DashboardView_LayoutUpdated(object sender, EventArgs e) => UpdateGlassCardMask();

        /// <summary>Clips imgLogoBlurred down to the union of every glass
        /// card's current on-screen rounded rect (in RootGrid's coordinate
        /// space, which imgLogoBlurred exactly fills). Everywhere outside
        /// that union, imgLogoBlurred is invisible and the sharp
        /// imgLogoWatermark underneath shows straight through — so only the
        /// patch of logo actually behind a card ever looks blurred, and it
        /// tracks that card live as the page resizes or scrolls.</summary>
        private void UpdateGlassCardMask()
        {
            if (imgLogoBlurred == null || _glassCards.Count == 0) return;

            var group = new GeometryGroup { FillRule = FillRule.Nonzero };

            foreach (var card in _glassCards)
            {
                if (card.ActualWidth <= 0 || card.ActualHeight <= 0) continue;

                Point topLeft;
                try
                {
                    topLeft = card.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
                }
                catch (InvalidOperationException)
                {
                    // Card isn't connected to RootGrid right now (e.g. mid-teardown) — skip it.
                    continue;
                }

                var rect = new Rect(topLeft, new Size(card.ActualWidth, card.ActualHeight));
                group.Children.Add(new RectangleGeometry(rect, 12, 12));
            }

            imgLogoBlurred.Clip = group;
        }

        /// <summary>Recursive visual-tree walk to collect every element of
        /// type T under root — used to find all CardStyle Borders without
        /// having to x:Name and list each one by hand.</summary>
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                    yield return typed;

                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        /// <summary>Loads the dashboard background logo from the embedded
        /// resource into both the sharp layer (imgLogoWatermark) and the
        /// blurred layer (imgLogoBlurred) — same frozen bitmap, safe to
        /// share across two Image controls. Best-effort: missing resource
        /// just means no watermark — never blocks dashboard loading.</summary>
        private void LoadWatermarkLogo()
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri("pack://application:,,,/Resources/dashboard-logo.png", UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                imgLogoWatermark.Source = bitmap;
                imgLogoBlurred.Source = bitmap;
            }
            catch
            {
                // Cosmetic failure should not block dashboard initialization
            }
        }

        private static string Greeting()
        {
            int h = DateTime.Now.Hour;
            string time = h < 12 ? "morning" : h < 17 ? "afternoon" : "evening";
            return $"Good {time} 👋";
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadData();

        private void LoadData()
        {
            DashboardSummary summary;
            try
            {
                summary = _repo.GetSummary();
            }
            catch (Exception ex)
            {
                // Don't let a schema mismatch blank the whole dashboard —
                // surface it plainly so it's obvious this needs wiring up.
                txtGreeting.Text = "Dashboard data unavailable";
                txtToday.Text = ex.Message;
                return;
            }

            txtBillCount.Text = summary.TodayBillCount.ToString();
            txtSalesAmount.Text = "₹" + summary.TodaySalesAmount.ToString("N0");
            txtProfit.Text = "₹" + summary.TodayProfit.ToString("N0");
            txtItemsSold.Text = summary.TodayItemsSold.ToString("0.##");
            txtCashInHand.Text = "₹" + summary.CashInHand.ToString("N0");

            TopItemsList.ItemsSource = summary.TopItemsToday;
            txtNoTopItems.Visibility = summary.TopItemsToday.Any() ? Visibility.Collapsed : Visibility.Visible;

            LowStockList.ItemsSource = summary.LowStockItems;
            txtNoLowStock.Visibility = summary.LowStockItems.Any() ? Visibility.Collapsed : Visibility.Visible;

            RecentBillsList.ItemsSource = summary.RecentBills;
            txtNoRecentBills.Visibility = summary.RecentBills.Any() ? Visibility.Collapsed : Visibility.Visible;

            DrawChart(summary.Last7Days);
        }

        /// <summary>
        /// Hand-rolled bar chart — one column per day inside ChartGrid.
        /// Deliberately not pulling in a charting library for a 7-bar chart;
        /// if the dashboard grows more chart types later, swap this for
        /// LiveCharts2 or similar and this method just goes away.
        /// </summary>
        private void DrawChart(System.Collections.Generic.List<DayTotal> days)
        {
            ChartGrid.Children.Clear();
            ChartGrid.ColumnDefinitions.Clear();
            ChartGrid.RowDefinitions.Clear();

            if (days == null || days.Count == 0) return;

            decimal max = days.Max(d => d.Amount);
            if (max <= 0) max = 1;

            foreach (var _ in days)
                ChartGrid.ColumnDefinitions.Add(new ColumnDefinition());

            const double chartHeight = 150; // leaves room below for value + day labels

            for (int i = 0; i < days.Count; i++)
            {
                var day = days[i];
                double heightRatio = (double)(day.Amount / max);
                double barHeight = Math.Max(4, heightRatio * chartHeight);
                bool isToday = day.Date.Date == DateTime.Today;

                var col = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0)
                };

                col.Children.Add(new TextBlock
                {
                    Text = day.Amount >= 1000 ? $"{day.Amount / 1000:0.#}k" : day.Amount.ToString("0"),
                    FontSize = 10,
                    Foreground = (Brush)new BrushConverter().ConvertFrom("#8890A8"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                });

                col.Children.Add(new Border
                {
                    Width = 26,
                    Height = barHeight,
                    CornerRadius = new CornerRadius(5, 5, 2, 2),
                    Background = (Brush)new BrushConverter().ConvertFrom(isToday ? "#1F5C9E" : "#C6DAEC")
                });

                col.Children.Add(new TextBlock
                {
                    Text = day.Date.ToString("ddd"),
                    FontSize = 11,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = (Brush)new BrushConverter().ConvertFrom(isToday ? "#1A1A2E" : "#8890A8"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                });

                Grid.SetColumn(col, i);
                ChartGrid.Children.Add(col);
            }
        }

        // ── Recent Bills — double-click a row to open it in Sales Entry ─────
        // One handler on the ListBox itself (not per-row) — MouseDoubleClick
        // bubbles up from whichever row was clicked, and RecentBillsList
        // .SelectedItem is already that row by the time this fires (WPF
        // updates selection on mouse-down, before Click/DoubleClick raise).
        // The item is whatever DashboardRepository.GetSummary() put in
        // summary.RecentBills — read via reflection rather than a named
        // type, since that model lives in DashboardRepository (not part of
        // this view) and the only thing needed off it is FormattedBillNo,
        // which the ItemTemplate above already binds. Reuses
        // EntryDrillDownHelper.NavigateToSalesBill — the exact same
        // FormattedBillNo -> bill lookup the Daybook/Ledger Report's own
        // double-click already relies on, so behavior (including the "bill
        // not found" message if it was since deleted) matches those reports.
        private void RecentBillsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var selected = RecentBillsList.SelectedItem;
            if (selected == null) return;

            var prop = selected.GetType().GetProperty("FormattedBillNo");
            if (prop?.GetValue(selected) is not string formattedBillNo || string.IsNullOrWhiteSpace(formattedBillNo))
                return;

            EntryDrillDownHelper.NavigateToSalesBill(formattedBillNo);
        }

        // ── Quick actions — reuse MainWindow's existing tab registry ────────
        private void BtnNewSale_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenOrActivateTitle("Sales Entry");
        private void BtnNewPurchase_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenOrActivateTitle("Purchase Entry");
        private void BtnReceipt_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenOrActivateTitle("Receipt Entry");
        private void BtnPayment_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenOrActivateTitle("Payments Entry");
        private void BtnStockReport_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenOrActivateTitle("Current Stock Report");
        private void BtnDaybook_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenOrActivateTitle("Daybook Report (Taxwise)");
    }
}