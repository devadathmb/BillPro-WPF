using System.Linq;
using BillPro.Repositories;

namespace BillPro.Helpers
{
    // Shared by ReportPrintHelper / ReportPdfHelper / ReportExcelHelper so
    // the exact same "Company Name" + "Address" pair appears as the very
    // first thing on every printed/exported report — sourced from Company
    // Details, the same CompanyRepository.CompanyName / .Address fields the
    // invoice letterhead and Login screen already read from. Read fresh on
    // every call (not cached) since Company Details can be edited while a
    // report tab is still open.
    internal static class ReportCompanyHeader
    {
        public static (string Name, string Address) GetLines()
        {
            try
            {
                var repo = new CompanyRepository();
                repo.InitializeDatabase();
                var company = repo.Get();

                string name = string.IsNullOrWhiteSpace(company?.CompanyName)
                    ? "Your Company Name"
                    : company.CompanyName.Trim();

                // Address is stored multi-line (see InvoicePrintHelper's own
                // handling of company.Address) — collapsed to one line here
                // since this is a compact heading, not a full letterhead block.
                string address = string.IsNullOrWhiteSpace(company?.Address)
                    ? ""
                    : string.Join(", ", company.Address
                        .Split('\n')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0));

                return (name, address);
            }
            catch
            {
                // Cosmetic-only heading — never let a DB hiccup block a report
                // from printing/exporting.
                return ("REPORT", "");
            }
        }
    }
}