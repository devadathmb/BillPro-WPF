using System.Collections.Generic;

namespace BillPro.Helpers
{
    // ═══════════════════════════════════════════════════════════════════════
    // INVOICE DATA MODELS
    // Plain data carried from SalesEntryView into InvoicePrintHelper /
    // InvoiceExcelHelper. Deliberately decoupled from SalesSummary/SalesLine
    // (the DB models) — this is the "what goes on the page" shape, built by
    // SalesEntryView.BuildInvoiceData(), not something the repository knows
    // or cares about.
    // ═══════════════════════════════════════════════════════════════════════
    public class InvoiceData
    {
        // ── Letterhead — populated in SalesEntryView.BuildInvoiceData from
        // CompanyRepository.Get() (company_details table, edited via
        // CompanyDetailsView). Defaults below only cover the case where
        // that screen has never been filled in yet. ──────────────────────
        public string CompanyName { get; set; } = "";
        public string CompanyRegNumber { get; set; } = "";
        public List<string> CompanyAddressLines { get; set; } = new();

        public string CompanyPhone1Label { get; set; } = "";
        public string CompanyPhone1 { get; set; } = "";
        public string CompanyPhone2Label { get; set; } = "";
        public string CompanyPhone2 { get; set; } = "";
        public string CompanyEmail { get; set; } = "";

        // Absolute path to the logo image file, copied from
        // CompanyDetails.LogoPath (company_details table). Used on the
        // letterhead in place of the initial-letter placeholder, and as
        // a faint watermark behind the invoice body. Blank/missing file
        // falls back to the old placeholder — no watermark is drawn.
        public string CompanyLogoPath { get; set; } = "";

        public string CompanyBank1Name { get; set; } = "";
        public string CompanyBank1AccountNo { get; set; } = "";
        public string CompanyBank1AccountHolder { get; set; } = "";
        public string CompanyBank2Name { get; set; } = "";
        public string CompanyBank2AccountNo { get; set; } = "";
        public string CompanyBank2AccountHolder { get; set; } = "";

        // ── Compatibility aliases — InvoiceExcelHelper (and anything else
        // built against the old shape) still reads CompanyPhone/CompanyGstin.
        // Left in place rather than touched blind, since that file wasn't
        // part of this pass; CompanyGstin now has no source (GSTIN was
        // dropped from the letterhead in favour of Reg. Number) so it's
        // just blank unless set explicitly. ─────────────────────────────
        public string CompanyPhone => CompanyPhone1;
        public string CompanyGstin { get; set; } = "";

        public string InvoiceTitle { get; set; } = "TAX INVOICE";
        public string FormattedBillNo { get; set; }
        public string BillDate { get; set; }

        public string TransportMode { get; set; }
        public string VehicleNo { get; set; }

        public InvoiceParty BillTo { get; set; } = new();
        public InvoiceParty ShipTo { get; set; } = new();   // same as BillTo unless a separate ship-to exists later

        public bool IsInterstate { get; set; }

        public List<InvoiceLine> Lines { get; set; } = new();

        public double RoundOff { get; set; }
        public double NetTotal { get; set; }
    }

    public class InvoiceParty
    {
        public string Name { get; set; }
        public List<string> AddressLines { get; set; } = new();
        public string State { get; set; }
        public string StateCode { get; set; }
        public string Phone { get; set; }
        public string Mobile { get; set; }
        public string Gstin { get; set; }
    }

    public class InvoiceLine
    {
        public int SlNo { get; set; }
        public string Particulars { get; set; }
        public string HsnCode { get; set; }
        public double Rate { get; set; }
        public double Qty { get; set; }
        public string Uom { get; set; } = "NOS";
        public double GrossAmt { get; set; }
        public double DiscAmt { get; set; }
        public double TaxableValue { get; set; }
        public double TaxPercent { get; set; }
        public double TaxAmount { get; set; }
        public double Total { get; set; }
    }
}