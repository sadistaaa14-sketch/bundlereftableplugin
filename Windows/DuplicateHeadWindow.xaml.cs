using Frosty.Controls;
using System.Windows;

namespace BundleRefTablePlugin.Windows
{
    public partial class DuplicateHeadWindow : FrostyDockableWindow
    {
        public string SourceFolder { get; private set; }
        public string NewFolder { get; private set; }
        public string HostFolder { get; private set; }

        public DuplicateHeadWindow(string sourceFolder)
        {
            InitializeComponent();
            SourceFolder = sourceFolder;
            sourceFolderTextBox.Text = sourceFolder;
            newFolderTextBox.Text = sourceFolder;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            string newFolder = newFolderTextBox.Text.Replace('\\', '/').Trim('/');
            string hostFolder = hostFolderTextBox.Text.Replace('\\', '/').Trim('/');

            if (string.IsNullOrEmpty(newFolder))
            {
                FrostyMessageBox.Show("New folder path cannot be empty.", "Frosty Editor");
                return;
            }

            if (newFolder.Equals(SourceFolder, System.StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New folder must be different from the source folder.", "Frosty Editor");
                return;
            }

            NewFolder = newFolder;
            HostFolder = string.IsNullOrEmpty(hostFolder) ? SourceFolder : hostFolder;
            DialogResult = true;
            Close();
        }
    }
}
