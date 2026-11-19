using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BillPro.Helpers
{
    public static class CloseTabBehavior
    {
        public static readonly DependencyProperty CloseOnClickProperty =
            DependencyProperty.RegisterAttached(
                "CloseOnClick", typeof(bool), typeof(CloseTabBehavior),
                new PropertyMetadata(false, OnCloseOnClickChanged));

        public static void SetCloseOnClick(UIElement element, bool value) => element.SetValue(CloseOnClickProperty, value);
        public static bool GetCloseOnClick(UIElement element) => (bool)element.GetValue(CloseOnClickProperty);

        private static void OnCloseOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Button button)
            {
                // Prevent memory leaks by unsubscribing first
                button.Click -= Button_Click;

                if ((bool)e.NewValue)
                {
                    button.Click += Button_Click;
                }
            }
        }

        private static void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DependencyObject current)
            {
                // Walk up the visual tree until we find the AvalonDock tab hosting this button
                while (current != null)
                {
                    current = VisualTreeHelper.GetParent(current);

                    if (current is AvalonDock.Controls.LayoutDocumentControl ctrl)
                    {
                        if (ctrl.Model is AvalonDock.Layout.LayoutDocument document)
                        {
                            document.Close(); // Close the tab!
                            return;
                        }
                    }
                }
            }
        }
    }
}