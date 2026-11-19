using System;

namespace BillPro.Helpers
{
    // Converts a rupee amount to words using the Indian numbering system
    // (Crore / Lakh / Thousand / Hundred — not the Western Million/Billion
    // grouping), matching how Indian tax invoices conventionally spell out
    // amounts. Paise are rounded off; invoices here don't carry sub-rupee
    // precision in the printed total.
    public static class AmountInWordsConverter
    {
        private static readonly string[] Ones =
        {
            "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine",
            "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen",
            "Seventeen", "Eighteen", "Nineteen"
        };

        private static readonly string[] Tens =
        {
            "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"
        };

        public static string Convert(double amount)
        {
            long rupees = (long)Math.Round(amount, MidpointRounding.AwayFromZero);

            if (rupees == 0) return "Rupees Zero only";

            string words = ConvertWhole(rupees);
            return $"Rupees {words} only";
        }

        private static string ConvertWhole(long number)
        {
            if (number == 0) return "";

            long crore = number / 10000000;
            number %= 10000000;
            long lakh = number / 100000;
            number %= 100000;
            long thousand = number / 1000;
            number %= 1000;
            long hundred = number / 100;
            long rest = number % 100;

            var parts = new System.Collections.Generic.List<string>();

            if (crore > 0) parts.Add(ConvertTwoDigitGroup(crore) + " Crore");
            if (lakh > 0) parts.Add(ConvertTwoDigitGroup(lakh) + " Lakh");
            if (thousand > 0) parts.Add(ConvertTwoDigitGroup(thousand) + " Thousand");
            if (hundred > 0) parts.Add(Ones[hundred] + " Hundred");
            if (rest > 0) parts.Add(ConvertTwoDigitGroup(rest));

            return string.Join(" ", parts);
        }

        // Handles a 1–2 digit group (0–99), used for the crore/lakh/thousand
        // segments which are themselves always < 100 after division above.
        private static string ConvertTwoDigitGroup(long n)
        {
            if (n < 20) return Ones[n];
            return Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "");
        }
    }
}