using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BillPro.Controls
{
    /// <summary>
    /// One entry in the sidebar's menu tree. A node with Children behaves as
    /// a submenu launcher (hover/click opens a Popup to the right); a node
    /// with no Children is a leaf that fires OnClick and collapses the
    /// whole cascade.
    /// </summary>
    public class NavNode
    {
        public string Title { get; set; }
        /// <summary>Small glyph shown to the left of the title, e.g. "📄", "▤", "$". Optional.</summary>
        public string Icon { get; set; }
        public List<NavNode> Children { get; } = new List<NavNode>();
        public Action OnClick { get; set; }
        /// <summary>Top-level section header (MASTER FILES, ENTRIES, ...) gets slightly bolder styling.</summary>
        public bool IsSection { get; set; }

        public bool HasChildren => Children.Count > 0;

        public NavNode(string title, Action onClick = null, string icon = null)
        {
            Title = title;
            OnClick = onClick;
            Icon = icon;
        }

        public NavNode Add(NavNode child)
        {
            if (child == null) return null;
            Children.Add(child);
            return child;
        }
    }

    /// <summary>
    /// Builds and drives a classic cascading flyout menu (like an old
    /// Windows Start Menu / File menu) into a host Panel. Top-level nodes
    /// are rendered as normal buttons stacked vertically in the host panel;
    /// any node with children opens a Popup docked to its right showing the
    /// children, and a child with its own children opens a further Popup to
    /// ITS right — recursively, as deep as the tree goes.
    ///
    /// Hovering a sibling swaps the open submenu immediately. Leaving the
    /// whole cascade (button + all open popups) for ~300ms closes it. A
    /// leaf click always closes everything.
    /// </summary>
    public class CascadingSidebarMenu
    {
        private readonly Panel _hostPanel;
        // One open popup per depth level: chain[0] = popup opened from a
        // top-level button, chain[1] = popup opened from an item inside
        // chain[0], etc.
        private readonly List<Popup> _chain = new List<Popup>();
        private readonly DispatcherTimer _closeTimer;

        // Root-level "currently active" top button, so we can highlight it
        // while its cascade is open.
        private Button _activeTopButton;

        public CascadingSidebarMenu(Panel hostPanel)
        {
            _hostPanel = hostPanel;
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _closeTimer.Tick += (s, e) => { _closeTimer.Stop(); CloseFrom(0); };
        }

        public void Build(IEnumerable<NavNode> topLevelNodes)
        {
            _hostPanel.Children.Clear();
            foreach (var node in topLevelNodes)
            {
                _hostPanel.Children.Add(CreateNodeButton(node, depth: 0, isTopLevel: true));
            }
        }

        // ── Button construction ────────────────────────────────────────────

        private Button CreateNodeButton(NavNode node, int depth, bool isTopLevel)
        {
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources[isTopLevel ? "NavFlyoutTopBtn" : "NavFlyoutItemBtn"],
                Tag = node
            };

            // Grid instead of a horizontal StackPanel: the title column is
            // "*" (stretches to fill the button), so the arrow column always
            // lands flush against the button's right edge — regardless of
            // how long/short the title text is — instead of trailing right
            // after the text like it did with a StackPanel.
            var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // arrow

            if (!string.IsNullOrEmpty(node.Icon))
            {
                var iconBlock = new TextBlock
                {
                    Text = node.Icon,
                    FontSize = isTopLevel ? 15 : 13,
                    Width = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["NavIconBrush"]
                };
                Grid.SetColumn(iconBlock, 0);
                row.Children.Add(iconBlock);
            }

            var titleBlock = new TextBlock
            {
                Text = node.Title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = node.IsSection ? FontWeights.Bold : FontWeights.Normal
            };
            Grid.SetColumn(titleBlock, 1);
            row.Children.Add(titleBlock);

            if (node.HasChildren)
            {
                var arrowBlock = new TextBlock
                {
                    Text = "›",
                    FontSize = 15,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = (Brush)Application.Current.Resources["NavArrowBrush"]
                };
                Grid.SetColumn(arrowBlock, 2);
                row.Children.Add(arrowBlock);
            }
            btn.Content = row;

            btn.MouseEnter += (s, e) =>
            {
                _closeTimer.Stop();
                if (node.HasChildren)
                {
                    if (isTopLevel) _activeTopButton = btn;
                    OpenSubmenu(node, btn, depth);
                }
                else
                {
                    // Hovering a leaf sibling should still collapse any
                    // deeper cascade left open from a previous hover.
                    CloseFrom(depth);
                }
            };
            btn.MouseLeave += (s, e) => _closeTimer.Start();

            btn.Click += (s, e) =>
            {
                if (!node.HasChildren)
                {
                    node.OnClick?.Invoke();
                    CloseFrom(0);
                    e.Handled = true;
                }
                else
                {
                    // Click on a folder node (e.g. touch users / no-hover):
                    // toggle its submenu explicitly.
                    if (isTopLevel) _activeTopButton = btn;
                    OpenSubmenu(node, btn, depth);
                    e.Handled = true;
                }
            };

            return btn;
        }

        // ── Popup management ────────────────────────────────────────────────

        private void OpenSubmenu(NavNode node, FrameworkElement anchor, int depth)
        {
            // Already open for this exact anchor at this depth? Leave it be.
            if (_chain.Count > depth && ReferenceEquals(_chain[depth].PlacementTarget, anchor))
            {
                CloseFrom(depth + 1);
                return;
            }

            // A different item at this depth (or a deeper one) is open — collapse it first.
            CloseFrom(depth);

            var itemsPanel = new StackPanel { Margin = new Thickness(6) };
            foreach (var child in node.Children)
                itemsPanel.Children.Add(CreateNodeButton(child, depth + 1, isTopLevel: false));

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["NavFlyoutCardBrush"],
                BorderBrush = (Brush)Application.Current.Resources["NavFlyoutBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = itemsPanel,
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 18, ShadowDepth = 3, Direction = 260 }
            };
            card.MouseEnter += (s, e) => _closeTimer.Stop();
            card.MouseLeave += (s, e) => _closeTimer.Start();

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Right,
                HorizontalOffset = 2,
                VerticalOffset = -8,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = card,
                IsOpen = true
            };

            _chain.Add(popup);
        }

        /// <summary>Closes every open popup from the given depth onward (inclusive).</summary>
        private void CloseFrom(int depth)
        {
            for (int i = _chain.Count - 1; i >= depth; i--)
            {
                _chain[i].IsOpen = false;
                _chain.RemoveAt(i);
            }
            if (depth == 0) _activeTopButton = null;
        }

        /// <summary>Call from the window's PreviewMouseDown so clicking anywhere
        /// outside the sidebar/cascade closes it immediately.</summary>
        public void CloseAllIfClickedOutside(IInputElement clickedElement)
        {
            if (clickedElement is DependencyObject d)
            {
                if (IsDescendantOfHost(d)) return;
                foreach (var p in _chain)
                    if (p.Child is DependencyObject pd && IsDescendant(d, pd)) return;
            }
            CloseFrom(0);
        }

        private bool IsDescendantOfHost(DependencyObject d) => IsDescendant(d, _hostPanel);

        private static bool IsDescendant(DependencyObject child, DependencyObject potentialAncestor)
        {
            var current = child;
            while (current != null)
            {
                if (ReferenceEquals(current, potentialAncestor)) return true;
                current = (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}