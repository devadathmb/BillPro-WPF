using System;
using System.Windows.Data;

namespace BillPro.Views
{
    /// <summary>Groups every item into the same bucket — used purely so
    /// SmartComboBox's GroupStyle.HeaderTemplate renders once above the
    /// dropdown as a static column-header row (Code/Item Name/Batch/MRP/
    /// Purch. Rate/Stock), matching Sales Return Entry's item picker. There
    /// is never more than one real group. Shared by ExcessStockEntryView and
    /// ShortStockEntryView (both use the same item/batch picker pattern).</summary>
    public class ConstantGroupConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => string.Empty;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}