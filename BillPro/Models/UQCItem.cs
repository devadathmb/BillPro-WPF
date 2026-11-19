namespace BillPro.Models
{
    public class UQCItem
    {
        public string UQC { get; set; }
        public string Description { get; set; }

        // This combines both fields for the ComboBox (e.g., "NOS - NUMBERS")
        public string DisplayText => $"{UQC} - {Description}";
    }
}