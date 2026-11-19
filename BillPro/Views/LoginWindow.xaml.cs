using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BillPro.Helpers;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class LoginWindow : Window
    {
        private readonly UserRepository _userRepo = new UserRepository();
        private readonly CompanyRepository _companyRepo = new CompanyRepository();

        public LoginWindow()
        {
            InitializeComponent();

            LayoutUpdated += LoginWindow_LayoutUpdated;

            Loaded += (s, e) =>
            {
                _userRepo.InitializeDatabase();
                LoadBackgroundLogo();
                LoadCompanyName();
                txtUsername.Focus();
            };
        }

        private void LoginWindow_LayoutUpdated(object sender, EventArgs e) => UpdateGlassCardMask();

        /// <summary>
        /// Clips imgLogoBlurred to GlassCard's current on-screen rounded
        /// rect. THE FIX vs. the previous version: Clip geometry on a
        /// UIElement is always interpreted in that element's OWN local
        /// coordinate space — not its parent's, not RootGrid's. Computing
        /// the rect via GlassCard.TransformToAncestor(RootGrid) gave
        /// coordinates in RootGrid's space, which is why the blurred patch
        /// rendered offset from the card. Using
        /// GlassCard.TransformToVisual(imgLogoBlurred) instead gives the
        /// rect directly in imgLogoBlurred's own space, so it lines up
        /// exactly regardless of how the image itself is stretched or
        /// positioned. (Also, with both logo Images now set to
        /// Stretch="UniformToFill" + Stretch alignment filling RootGrid
        /// completely, their local origin already matches RootGrid's
        /// anyway — belt and braces.)
        /// </summary>
        private void UpdateGlassCardMask()
        {
            if (imgLogoBlurred == null || GlassCard == null) return;
            if (GlassCard.ActualWidth <= 0 || GlassCard.ActualHeight <= 0) return;

            Point topLeft;
            try
            {
                topLeft = GlassCard.TransformToVisual(imgLogoBlurred).Transform(new Point(0, 0));
            }
            catch (InvalidOperationException)
            {
                return; // not connected to the visual tree yet
            }

            var rect = new Rect(topLeft, new Size(GlassCard.ActualWidth, GlassCard.ActualHeight));
            imgLogoBlurred.Clip = new RectangleGeometry(rect, 16, 16);
        }

        /// <summary>
        /// Loads the same dashboard-logo.png DashboardView uses, from the
        /// embedded WPF resource (compiled into the assembly), as the
        /// full-window background here — sharp copy for imgLogoWatermark,
        /// blurred copy (clipped to the card) for imgLogoBlurred. Works in
        /// compiled/published builds, unlike the old solution-folder
        /// lookup. If the resource is missing, both Images simply stay
        /// without a source and the plain #F4F5FA backdrop is still a
        /// complete background.
        /// </summary>
        private void LoadBackgroundLogo()
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
                // Cosmetic only — never block login over a missing/corrupt logo file.
            }
        }

        /// <summary>
        /// Company name shown on the glass card, pulled from Company
        /// Details — falls back to a generic placeholder if nothing's
        /// configured yet.
        /// </summary>
        private void LoadCompanyName()
        {
            try
            {
                _companyRepo.InitializeDatabase();
                var company = _companyRepo.Get();

                string name = string.IsNullOrWhiteSpace(company?.CompanyName)
                    ? "Your Company Name"
                    : company.CompanyName.Trim();

                const int softLimit = 20;
                if (name.Length > softLimit)
                {
                    int splitAt = name.LastIndexOf(' ', Math.Min(softLimit, name.Length - 1));
                    if (splitAt > 0)
                    {
                        txtCompanyNameLine1.Text = name.Substring(0, splitAt);
                        txtCompanyNameLine2.Text = name.Substring(splitAt + 1);
                        txtCompanyNameLine2.Visibility = Visibility.Visible;
                        return;
                    }
                }

                txtCompanyNameLine1.Text = name;
            }
            catch
            {
                // Leave the XAML placeholder text in place.
            }
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnLogin_Click(sender, e);
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            txtError.Visibility = Visibility.Collapsed;

            string username = txtUsername.Text.Trim();
            string password = txtPassword.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                ShowError("Enter both username and password.");
                return;
            }

            btnLogin.IsEnabled = false;
            txtBusy.Visibility = Visibility.Visible;

            Dispatcher.InvokeAsync(() =>
            {
                var result = _userRepo.TryLogin(username, password);

                if (!result.Success)
                {
                    btnLogin.IsEnabled = true;
                    txtBusy.Visibility = Visibility.Collapsed;
                    ShowError("Incorrect username or password.");
                    txtPassword.Clear();
                    txtPassword.Focus();
                    return;
                }

                UserSession.Start(result.UserId, username, result.DisplayName, result.Role,
                    result.CanEdit, result.CanDelete, result.DeniedFormKeys);

                var main = new MainWindow();
                Application.Current.MainWindow = main;
                main.Show();
                Close();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ShowError(string message)
        {
            txtError.Text = message;
            txtError.Visibility = Visibility.Visible;
        }
    }
}