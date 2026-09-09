using System.Windows;
using DesktopBuckets.Interop;

namespace DesktopBuckets.Views
{
    public partial class InputDialog : Window
    {
        public string ResponseText => Input.Text.Trim();

        public InputDialog(string title, string prompt, string initial = "", string okLabel = "Create")
        {
            InitializeComponent();
            WindowChromeHelper.Attach(this);
            Title = title;
            PromptText.Text = prompt;
            Input.Text = initial;
            OkButton.Content = okLabel;
            Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Input.Text))
            {
                Input.Focus();
                return;
            }
            DialogResult = true;
        }

        /// <summary>Shows the dialog modally; returns the entered text or null if cancelled.</summary>
        public static string? Ask(string title, string prompt, string initial = "", string okLabel = "Create", Window? owner = null)
        {
            var dlg = new InputDialog(title, prompt, initial, okLabel);
            if (owner != null) dlg.Owner = owner;
            return dlg.ShowDialog() == true ? dlg.ResponseText : null;
        }
    }
}
