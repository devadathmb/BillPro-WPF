using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BillPro.Helpers
{
    public static class FocusBehavior
    {
        // ────────────────────────────────────────────────────────────────────────
        // 1. MoveOnEnter (Automatically acts like the Tab key)
        // ────────────────────────────────────────────────────────────────────────

        public static readonly DependencyProperty MoveOnEnterProperty =
            DependencyProperty.RegisterAttached(
                "MoveOnEnter", typeof(bool), typeof(FocusBehavior),
                new PropertyMetadata(false, OnMoveOnEnterChanged));

        public static void SetMoveOnEnter(UIElement element, bool value) => element.SetValue(MoveOnEnterProperty, value);
        public static bool GetMoveOnEnter(UIElement element) => (bool)element.GetValue(MoveOnEnterProperty);

        private static void OnMoveOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                element.PreviewKeyDown -= Element_PreviewKeyDown_Move; // Prevent memory leaks
                if ((bool)e.NewValue)
                    element.PreviewKeyDown += Element_PreviewKeyDown_Move;
            }
        }

        private static void Element_PreviewKeyDown_Move(object sender, KeyEventArgs e)
        {
            // Note: We ignore SmartComboBox here because we already programmed it to handle its own Enter key!
            if (e.Key == Key.Return && sender is not BillPro.Controls.SmartComboBox)
            {
                e.Handled = true;
                if (sender is UIElement element)
                {
                    // Move focus to the next logical UI element in the Tab order
                    element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // 2. TargetOnEnter (Jumps to a specific, named control)
        // ────────────────────────────────────────────────────────────────────────

        public static readonly DependencyProperty TargetOnEnterProperty =
            DependencyProperty.RegisterAttached(
                "TargetOnEnter", typeof(UIElement), typeof(FocusBehavior),
                new PropertyMetadata(null, OnTargetOnEnterChanged));

        public static void SetTargetOnEnter(UIElement element, UIElement value) => element.SetValue(TargetOnEnterProperty, value);
        public static UIElement GetTargetOnEnter(UIElement element) => (UIElement)element.GetValue(TargetOnEnterProperty);

        private static void OnTargetOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                element.PreviewKeyDown -= Element_PreviewKeyDown_Target;
                if (e.NewValue != null)
                    element.PreviewKeyDown += Element_PreviewKeyDown_Target;
            }
        }

        private static void Element_PreviewKeyDown_Target(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && sender is not BillPro.Controls.SmartComboBox)
            {
                if (sender is UIElement element)
                {
                    UIElement target = GetTargetOnEnter(element);
                    if (target != null)
                    {
                        e.Handled = true;
                        target.Focus();

                        // UX Polish: If jumping to a textbox, automatically select all the text inside it
                        if (target is TextBox tb)
                        {
                            tb.SelectAll();
                        }
                    }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // 3. TargetOnTab (Jumps to a specific, named control on Tab)
        // ────────────────────────────────────────────────────────────────────────

        public static readonly DependencyProperty TargetOnTabProperty =
            DependencyProperty.RegisterAttached(
                "TargetOnTab", typeof(UIElement), typeof(FocusBehavior),
                new PropertyMetadata(null, OnTargetOnTabChanged));

        public static void SetTargetOnTab(UIElement element, UIElement value) => element.SetValue(TargetOnTabProperty, value);
        public static UIElement GetTargetOnTab(UIElement element) => (UIElement)element.GetValue(TargetOnTabProperty);

        private static void OnTargetOnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                element.PreviewKeyDown -= Element_PreviewKeyDown_TabTarget;
                if (e.NewValue != null)
                    element.PreviewKeyDown += Element_PreviewKeyDown_TabTarget;
            }
        }

        private static void Element_PreviewKeyDown_TabTarget(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                if (sender is UIElement element)
                {
                    UIElement target = GetTargetOnTab(element);
                    if (target != null)
                    {
                        e.Handled = true; // Prevents default WPF tab navigation
                        target.Focus();

                        // UX Polish: If jumping to a textbox, automatically select all the text inside it
                        if (target is TextBox tb)
                        {
                            tb.SelectAll();
                        }
                    }
                }
            }
        }
    }
}