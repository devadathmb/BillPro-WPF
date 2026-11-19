using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BillPro.Helpers;
using BillPro.Repositories;

namespace BillPro.Views
{
    public partial class MenuCustomizationView : UserControl
    {
        private readonly UserRepository _userRepo = new();

        public MenuCustomizationView()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadUsers();
        }

        private void LoadUsers()
        {
            _userRepo.InitializeDatabase();
            var users = _userRepo.GetNormalUsers();
            cmbUser.ItemsSource = users;

            if (users.Count > 0)
                cmbUser.SelectedIndex = 0;
            else
                treeMenu.ItemsSource = null;
        }

        private void CmbUser_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbUser.SelectedItem is not UserListItem user)
            {
                treeMenu.ItemsSource = null;
                return;
            }

            var denied = _userRepo.GetDeniedFormKeys(user.id);

            var roots = NavCatalog.BuildTree()
                .Select(n => new CheckableNavNode(n))
                .ToList();

            foreach (var root in roots)
                foreach (var deniedKey in denied)
                    root.SetLeafChecked(deniedKey, isChecked: false);

            treeMenu.ItemsSource = new ObservableCollection<CheckableNavNode>(roots);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (cmbUser.SelectedItem is not UserListItem user) return;
            if (treeMenu.ItemsSource is not ObservableCollection<CheckableNavNode> roots) return;

            var uncheckedLeaves = roots.SelectMany(r => r.GetUncheckedLeafTitles()).ToList();
            _userRepo.SetDeniedFormKeys(user.id, uncheckedLeaves);

            txtSavedFlag.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, args) => { txtSavedFlag.Visibility = Visibility.Collapsed; timer.Stop(); };
            timer.Start();

            MessageBox.Show(
                "Menu access updated. This user needs to log out and back in for the change to take effect.",
                "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Checkbox-tree wrapper around NavCatalogNode. Checking/unchecking a
    /// section or group cascades down to every descendant leaf — only leaf
    /// checked-state is actually persisted (section/group nodes are purely
    /// visual grouping, never saved as their own denial).
    /// </summary>
    public class CheckableNavNode : INotifyPropertyChanged
    {
        public string Title { get; }
        public bool IsLeaf { get; }
        public ObservableCollection<CheckableNavNode> Children { get; } = new();

        private bool _isChecked = true;

        public CheckableNavNode(NavCatalogNode source)
        {
            Title = source.Title;
            IsLeaf = source.IsLeaf;
            foreach (var child in source.Children)
                Children.Add(new CheckableNavNode(child));
        }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                OnPropertyChanged();
                foreach (var child in Children)
                    child.IsChecked = value;
            }
        }

        public IEnumerable<string> GetUncheckedLeafTitles()
        {
            if (IsLeaf)
            {
                if (!IsChecked) yield return Title;
                yield break;
            }
            foreach (var child in Children)
                foreach (var t in child.GetUncheckedLeafTitles())
                    yield return t;
        }

        /// <summary>Sets a specific leaf's checked state by title, without
        /// cascading back up to parents or down (a single leaf toggle).
        /// Used when loading a user's existing denial list.</summary>
        public void SetLeafChecked(string title, bool isChecked)
        {
            if (IsLeaf)
            {
                if (Title == title)
                {
                    _isChecked = isChecked;
                    OnPropertyChanged(nameof(IsChecked));
                }
                return;
            }
            foreach (var child in Children)
                child.SetLeafChecked(title, isChecked);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}