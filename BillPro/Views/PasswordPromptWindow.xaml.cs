using System.Windows;
using System.Windows.Input;

namespace BillPro.Views
{
    public partial class PasswordPromptWindow : Window
    {
        /// <summary>Set to true only after BtnOk_Click succeeds — DialogResult
        /// alone just tells the caller the dialog was submitted, not that the
        /// password was correct, so the caller still checks this.</summary>
        public bool Unlocked { get; private set; }

        public PasswordPromptWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => pwdBox.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => TryUnlock();

        private void PwdBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) TryUnlock();
        }

        private void TryUnlock()
        {
            if (BillPro.Helpers.PasswordHelper.Verify(pwdBox.Password))
            {
                Unlocked = true;
                DialogResult = true;
            }
            else
            {
                txtError.Visibility = Visibility.Visible;
                pwdBox.Clear();
                pwdBox.Focus();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}