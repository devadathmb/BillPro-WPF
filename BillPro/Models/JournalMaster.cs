// JournalMaster.cs
using System.ComponentModel;

namespace BillPro.Models
{
    // ─────────────────────────────────────────────────────────────────────
    // JOURNAL ENTRY — free-form double-entry voucher: any number of lines,
    // each hitting any a/c head as either a Debit or a Credit, each with
    // its own narration. No tax/GST concept at all (unlike Sales/Purchase/
    // Returns), so posting bypasses FinancialVoucher.Lines entirely and
    // goes straight to SettlementLegs, one per line — see
    // JournalEntryRepository.Accounts.cs.BuildVoucher.
    //
    // No series/prefix system — a single global auto-increment entry
    // number ("JV-1", "JV-2", ...), same convention as Sales Return /
    // Purchase Return.
    // ─────────────────────────────────────────────────────────────────────
    public class JournalEntrySummary
    {
        public int EntryNo { get; set; }              // PK, raw autoincrement
        public string EntryDate { get; set; }           // "yyyy-MM-dd"
        public double TotalAmount { get; set; }          // sum of Debit legs (== sum of Credit legs on a balanced voucher)
        public int CancelStatus { get; set; }

        public string CreatedBy { get; set; }
        public string CreatedDate { get; set; }
        public string LastModifiedBy { get; set; }
        public string LastModifiedDate { get; set; }
    }

    public class JournalEntryLine
    {
        public int EntryNo { get; set; }
        public int SlNo { get; set; }

        public string Mode { get; set; }               // "Debit" or "Credit"
        public string AcHeadCode { get; set; }
        public string AcHeadName { get; set; }           // denormalised for display/printing
        public string Narration { get; set; }
        public double Amount { get; set; }
    }

    // Grid row model for the entry screen — same conventions as
    // PurchaseReturnLine/SalesReturnLine (decimal, INotifyPropertyChanged).
    // DebitAmount/CreditAmount are derived so the grid can show them as two
    // separate columns (matching the reference Journal Entry form) without
    // the underlying line itself needing two amount fields.
    public class JournalGridLine : INotifyPropertyChanged
    {
        private string _mode = "Debit";
        private string _acHeadCode, _acHeadName, _narration;
        private decimal _amount;

        public string Mode
        {
            get => _mode;
            set { _mode = value; OnChanged(nameof(Mode)); OnChanged(nameof(DebitAmount)); OnChanged(nameof(CreditAmount)); }
        }

        public string AcHeadCode { get => _acHeadCode; set { _acHeadCode = value; OnChanged(nameof(AcHeadCode)); } }
        public string AcHeadName { get => _acHeadName; set { _acHeadName = value; OnChanged(nameof(AcHeadName)); } }
        public string Narration { get => _narration; set { _narration = value; OnChanged(nameof(Narration)); } }

        public decimal Amount
        {
            get => _amount;
            set { _amount = value; OnChanged(nameof(Amount)); OnChanged(nameof(DebitAmount)); OnChanged(nameof(CreditAmount)); }
        }

        // Editable pass-throughs — the grid's Credit/Debit columns bind
        // straight to these, so typing in either column both sets Amount
        // and flips Mode to match the column that was edited. A value of 0
        // is treated as "clear this side" rather than switching mode.
        public decimal DebitAmount
        {
            get => Mode == "Debit" ? Amount : 0;
            set
            {
                if (value == 0 && Mode != "Debit") return;
                Mode = "Debit";
                Amount = value;
            }
        }

        public decimal CreditAmount
        {
            get => Mode == "Credit" ? Amount : 0;
            set
            {
                if (value == 0 && Mode != "Credit") return;
                Mode = "Credit";
                Amount = value;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}