using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Syncfusion.UI.Xaml.Grid;

namespace BillPro.Helpers
{
    // ═══════════════════════════════════════════════════════════════════════
    // REUSABLE TABLE-SUMMARY ALIGNMENT SELECTOR
    //
    // Picks left- vs right-aligned styling per summary column, both built
    // from the single shared "ReportSummaryCellStyle" in GlobalStyles.xaml —
    // so no report needs to redeclare RightAlignStyle/LeftAlignStyle in its
    // own XAML anymore. Whichever column(s) hold text (item name, etc.) go
    // in LeftAlignColumns; everything else defaults to right-aligned, which
    // covers the common case of a report where only the description-style
    // column is text and every other summary column is numeric.
    //
    // Usage — this is the entire per-report wiring needed now:
    //
    //   <syncfusion:SfDataGrid.Resources>
    //       <helpers:SummaryStyleSelector x:Key="summaryStyleSelector"
    //                                    LeftAlignColumns="ItemName"/>
    //   </syncfusion:SfDataGrid.Resources>
    //
    //   <syncfusion:SfDataGrid.TableSummaryCellStyleSelector>
    //       <StaticResource ResourceKey="summaryStyleSelector"/>
    //   </syncfusion:SfDataGrid.TableSummaryCellStyleSelector>
    //
    // Multiple left-aligned columns: LeftAlignColumns="ItemName,Remarks".
    // Defaults to "ItemName" if not set at all, so existing reports work
    // unchanged even without specifying it.
    // ═══════════════════════════════════════════════════════════════════════
    public class SummaryStyleSelector : StyleSelector
    {
        public string LeftAlignColumns { get; set; } = "ItemName";

        private Style _leftStyle;
        private Style _rightStyle;

        public override Style SelectStyle(object item, DependencyObject container)
        {
            if (container is not GridTableSummaryCell cell || cell.ColumnBase?.GridColumn == null)
                return base.SelectStyle(item, container);

            string mappingName = cell.ColumnBase.GridColumn.MappingName;
            bool isLeft = LeftAlignColumns
                .Split(',')
                .Select(s => s.Trim())
                .Contains(mappingName, StringComparer.OrdinalIgnoreCase);

            return isLeft ? GetLeftStyle(cell) : GetRightStyle(cell);
        }

        private Style GetLeftStyle(FrameworkElement scope) => _leftStyle ??= BuildStyle(scope, HorizontalAlignment.Left);
        private Style GetRightStyle(FrameworkElement scope) => _rightStyle ??= BuildStyle(scope, HorizontalAlignment.Right);

        // Builds a left/right variant on top of the shared ReportSummaryCellStyle
        // (double-rule border, bold/larger text, etc.) defined once in
        // GlobalStyles.xaml — this is what lets every report share one visual
        // definition without copy-pasting Style blocks into each view's XAML.
        //
        // IMPORTANT: uses scope.TryFindResource(...), not
        // Application.Current.Resources[...]. The latter only finds the style
        // if GlobalStyles.xaml happens to be merged at the App.xaml level —
        // if it's merged per-view instead (common setup), that lookup
        // silently returns null and every FontSize/FontWeight/etc. edit to
        // ReportSummaryCellStyle would appear to do nothing, because the
        // built style was never actually inheriting from it.
        // TryFindResource walks the real resource tree (same resolution
        // {StaticResource} itself uses), so it works no matter where the
        // dictionary is merged.
        private static Style BuildStyle(FrameworkElement scope, HorizontalAlignment alignment)
        {
            var baseStyle = scope.TryFindResource("ReportSummaryCellStyle") as Style;

            var style = new Style(typeof(GridTableSummaryCell), baseStyle);
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, alignment));
            return style;
        }
    }
}