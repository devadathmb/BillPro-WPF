using System.Collections.Generic;
using System.Windows;

namespace BillPro.Helpers
{
    public class SearchConfig
    {
        // The title displayed at the top of the overlay
        public string Title { get; set; } = "Search";

        // The properties in your model that the text box should search through
        public List<string> SearchProperties { get; set; } = new List<string>();

        // The columns to generate in the DataGrid
        public List<SearchColumn> Columns { get; set; } = new List<SearchColumn>();

        public UIElement TargetOnSelect { get; set; }

        // --- DATE PROPERTIES ---
        public bool ShowDateFilter { get; set; } = false; // Default is false (hidden)
        public string DateFilterProperty { get; set; }    // e.g., "PurchaseDate"

        // --- SERIES (MULTI-SELECT) FILTER PROPERTIES ---
        // Mirrors the Series filter added to the three Sales reports:
        // shows a checklist dropdown + small selected-series label box in
        // the overlay's filter row when enabled. Empty selection = no
        // filter (all series). Wire this up the same way as the date
        // filter — the overlay reads ShowSeriesFilter/SeriesOptions to
        // decide whether to render the controls:SeriesFilterControl, and
        // filters SearchProperty == SeriesFilterProperty (e.g. "Prefix")
        // against the control's SelectedPrefixes.
        public bool ShowSeriesFilter { get; set; } = false;                 // Default is false (hidden)
        public string SeriesFilterProperty { get; set; }                    // e.g., "Prefix"
        public List<string> SeriesOptions { get; set; } = new List<string>(); // active series prefixes to list
    }

    public class SearchColumn
    {
        public string Header { get; set; }
        public string BindingPath { get; set; }

        // Defaults to "*" (fills remaining space), but can be set to "100", "Auto", etc.
        public string Width { get; set; } = "*";
        public string StringFormat { get; set; }

    }
}