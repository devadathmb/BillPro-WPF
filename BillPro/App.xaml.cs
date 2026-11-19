using Dapper; // Required for DefaultTypeMap
using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using BillPro.DataAccess;

namespace BillPro
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            RegisterSyncfusionLicense();

            DatabaseBootstrapper.InitializeAllDatabases();
            // Configure Dapper globally to map snake_case columns to PascalCase properties
            DefaultTypeMap.MatchNamesWithUnderscores = true;
        }

        private static void RegisterSyncfusionLicense()
        {
            string licenseKey = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");

            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                string secretsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secrets.local.json");
                if (File.Exists(secretsPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(secretsPath));
                    if (doc.RootElement.TryGetProperty("SyncfusionLicenseKey", out var keyProp))
                    {
                        licenseKey = keyProp.GetString();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(licenseKey))
            {
                Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(licenseKey);
            }
        }
    }
}