namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // COMPANY DETAILS  (System section — single settings row, id = 1)
    // Feeds the invoice letterhead. Editable from CompanyDetailsView,
    // persisted via CompanyRepository. Mirrors SalesSeriesMaster's plain-
    // POCO convention — no INotifyPropertyChanged needed since it isn't
    // bound to a live-editing grid, just loaded into a form and saved back.
    // ─────────────────────────────────────────────────────────────────────
    public class CompanyDetails
    {
        public int Id { get; set; } = 1;               // fixed single row

        public string CompanyName { get; set; }
        public string RegNumber { get; set; }

        // Kept as one multi-line field (operator types it exactly as it
        // should appear on the letterhead, one address segment per line)
        // rather than forcing it into rigid Street/City/Pin columns that
        // don't map cleanly onto how addresses are actually written here.
        public string Address { get; set; }

        // Two named phone lines, e.g. "Marketing (Nita)" / "082131723234"
        public string Phone1Label { get; set; }
        public string Phone1 { get; set; }
        public string Phone2Label { get; set; }
        public string Phone2 { get; set; }

        public string Email { get; set; }

        // Absolute path to the logo image file (jpg/png) on disk, copied
        // into the app's local data folder when chosen via CompanyDetailsView
        // so it survives even if the original file the user picked is later
        // moved or deleted. Used both on the invoice letterhead (in place of
        // the initial-letter placeholder) and as a faint watermark behind
        // the invoice body. Empty/null falls back to the old placeholder.
        public string LogoPath { get; set; }

        // Two bank accounts
        public string Bank1Name { get; set; }           // e.g. "BCA (Bank Central Asia)"
        public string Bank1AccountNo { get; set; }
        public string Bank1AccountHolder { get; set; }

        public string Bank2Name { get; set; }
        public string Bank2AccountNo { get; set; }
        public string Bank2AccountHolder { get; set; }

        // True once the row has been saved at least once. While true, the
        // Company Details screen is not reachable from the sidebar/first-run
        // flow — only via the hidden Ctrl+Shift+Alt+C shortcut gated by
        // PasswordHelper. Stored as 0/1 in company_details.is_locked.
        public bool IsLocked { get; set; }
    }
}