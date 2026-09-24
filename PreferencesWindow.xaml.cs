using System.Windows;
using Microsoft.Win32;

namespace UnityBrowser;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        var settings = AppSettings.Current;
        ExportFolderBox.Text = settings.ExportFolder;
        Select(MeshFormatBox, settings.MeshFormat);
        Select(ImageFormatBox, settings.ImageFormat);
        ExportAnimationsBox.IsChecked = settings.ExportAnimations;
        ExportSoundBox.IsChecked = settings.ExportSound;
    }

    private static void Select(System.Windows.Controls.ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];
    }

    private static string Tag(System.Windows.Controls.ComboBox box) =>
        (box.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose default export location" };
        if (Directory.Exists(ExportFolderBox.Text)) dlg.InitialDirectory = ExportFolderBox.Text;
        if (dlg.ShowDialog(this) == true) ExportFolderBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string folder = ExportFolderBox.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(folder))
        {
            var answer = MessageBox.Show(this, $"The folder does not exist:\n{folder}\n\nCreate it?",
                "Export Preferences", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not create the folder:\n{ex.Message}", "Export Preferences",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var settings = AppSettings.Current;
        settings.ExportFolder = folder;
        settings.MeshFormat = Tag(MeshFormatBox);
        settings.ImageFormat = Tag(ImageFormatBox);
        settings.ExportAnimations = ExportAnimationsBox.IsChecked == true;
        settings.ExportSound = ExportSoundBox.IsChecked == true;
        DialogResult = true;
    }
}
