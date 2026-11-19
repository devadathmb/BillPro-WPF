using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using BillPro.Models;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class CompanyDetailsView : UserControl
    {
        private readonly CompanyRepository _repo = new CompanyRepository();

        // Currently selected/loaded logo path (kept in sync with the
        // preview image and written back on save). Copied into a stable
        // app-local folder rather than referencing the user's original
        // file location, so it survives that file being moved/deleted.
        private string _logoPath = "";

        private static readonly string LogoStorageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BillPro", "CompanyLogo");

        /// <summary>Raised right after a successful save, once the row is
        /// locked. MainWindow subscribes to this to auto-close the tab so
        /// the screen never lingers open after the operator is done.</summary>
        public event System.EventHandler SavedAndLocked;

        public CompanyDetailsView()
        {
            InitializeComponent();
            _repo.InitializeDatabase();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Load();
        }

        private void Load()
        {
            var d = _repo.Get();

            txtCompanyName.Text = d.CompanyName;
            txtRegNumber.Text = d.RegNumber;
            txtAddress.Text = d.Address;

            txtPhone1Label.Text = d.Phone1Label;
            txtPhone1.Text = d.Phone1;
            txtPhone2Label.Text = d.Phone2Label;
            txtPhone2.Text = d.Phone2;

            txtEmail.Text = d.Email;

            _logoPath = d.LogoPath ?? "";
            ShowLogoPreview(_logoPath);

            txtBank1Name.Text = d.Bank1Name;
            txtBank1AccNo.Text = d.Bank1AccountNo;
            txtBank1AccHolder.Text = d.Bank1AccountHolder;

            txtBank2Name.Text = d.Bank2Name;
            txtBank2AccNo.Text = d.Bank2AccountNo;
            txtBank2AccHolder.Text = d.Bank2AccountHolder;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var d = new CompanyDetails
            {
                CompanyName = txtCompanyName.Text.Trim(),
                RegNumber = txtRegNumber.Text.Trim(),
                Address = txtAddress.Text.Trim(),

                Phone1Label = txtPhone1Label.Text.Trim(),
                Phone1 = txtPhone1.Text.Trim(),
                Phone2Label = txtPhone2Label.Text.Trim(),
                Phone2 = txtPhone2.Text.Trim(),

                Email = txtEmail.Text.Trim(),

                LogoPath = _logoPath,

                Bank1Name = txtBank1Name.Text.Trim(),
                Bank1AccountNo = txtBank1AccNo.Text.Trim(),
                Bank1AccountHolder = txtBank1AccHolder.Text.Trim(),

                Bank2Name = txtBank2Name.Text.Trim(),
                Bank2AccountNo = txtBank2AccNo.Text.Trim(),
                Bank2AccountHolder = txtBank2AccHolder.Text.Trim(),

                // Every save locks the screen again — whether this is the
                // first-run fill-in or a later edit via the hidden shortcut,
                // it should not stay reachable afterwards.
                IsLocked = true
            };

            _repo.Save(d);

            // Quick inline confirmation instead of a modal — same "don't
            // interrupt the operator" feel as the rest of the app.
            txtSavedFlag.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(2) };
            timer.Tick += (s, args) =>
            {
                txtSavedFlag.Visibility = Visibility.Collapsed;
                timer.Stop();
                SavedAndLocked?.Invoke(this, System.EventArgs.Empty);
            };
            timer.Start();
        }

        private void BtnBrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
                Title = "Choose Logo Image"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                Directory.CreateDirectory(LogoStorageDir);

                // Copy into app-local storage under a stable name (plus the
                // original extension) rather than keeping a reference to
                // wherever the user picked it from — same "copy it in, don't
                // just point at it" approach as everything else here.
                string ext = Path.GetExtension(dialog.FileName);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                string destPath = Path.Combine(LogoStorageDir, "logo" + ext);

                File.Copy(dialog.FileName, destPath, overwrite: true);

                _logoPath = destPath;
                ShowLogoPreview(_logoPath);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Could not load that image:\n" + ex.Message,
                    "Logo Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClearLogo_Click(object sender, RoutedEventArgs e)
        {
            _logoPath = "";
            ShowLogoPreview("");
        }

        private void ShowLogoPreview(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                imgLogoPreview.Source = null;
                return;
            }

            try
            {
                // Loaded from an in-memory byte buffer rather than
                // UriSource. UriSource made WPF cache the bitmap by file
                // path, which is a problem here since every logo is copied
                // to the same fixed filename (logo.jpg/.png) — a stale or
                // partially-written cache entry could be served instead of
                // the file that was just copied in. Reading the bytes
                // ourselves guarantees a fresh decode every time.
                byte[] bytes = File.ReadAllBytes(path);

                var bitmap = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                imgLogoPreview.Source = bitmap;
            }
            catch (System.Exception ex)
            {
                // Surfaced instead of swallowed — a blank preview box with
                // no explanation is exactly what made this bug hard to spot.
                imgLogoPreview.Source = null;
                MessageBox.Show("Could not display that logo image:\n" + ex.Message,
                    "Logo Preview Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
