using System.Windows;
using System.Windows.Controls;
using Mithu.App.ViewModels;

namespace Mithu.App.Views;

/// <summary>Desktop control settings. The view model owns the settings; this picks folders to add.</summary>
public partial class DesktopControlPage : UserControl
{
    public DesktopControlPage() => InitializeComponent();

    /// <summary>"Add disk": the disks inside this PC (not the Windows disk) that aren't allowed yet.</summary>
    private void AddDisk_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { DesktopControl: { } desktop } || sender is not FrameworkElement button)
        {
            return;
        }

        var menu = new ContextMenu { Style = (Style)FindResource("IslandMenu"), PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var itemStyle = (Style)FindResource("IslandMenuItem");
        var disks = desktop.AvailableDisks();
        foreach (var (root, name) in disks)
        {
            var item = new MenuItem { Header = name, Style = itemStyle };
            item.Click += (_, _) => desktop.AddFolder(root);
            menu.Items.Add(item);
        }

        if (disks.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No other disks to add", Style = (Style)FindResource("IslandMenuHeader") });
        }

        if (Window.GetWindow(this) is FloatingWindow window)
        {
            window.TrackMenu(menu);
        }

        menu.IsOpen = true;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { DesktopControl: { } desktop })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Choose a folder {Companion.Name} may use", Multiselect = false };
        var owner = Window.GetWindow(this);
        if (owner is FloatingWindow floating)
        {
            floating.TrackDialog(true); // don't collapse the panel while the picker is open
        }

        try
        {
            if (dialog.ShowDialog(owner) == true)
            {
                desktop.AddFolder(dialog.FolderName);
            }
        }
        finally
        {
            if (owner is FloatingWindow window)
            {
                window.TrackDialog(false);
            }
        }
    }
}
