using System.Windows;
using System.Windows.Controls;
using Mithu.App.ViewModels;

namespace Mithu.App.Views;

/// <summary>
/// AI settings page. A <see cref="PasswordBox"/> can't be bound, so its text is pushed to the view model here
/// (and cleared when the view model says so). The key never travels back into the box.
/// </summary>
public partial class AiSettingsPage : UserControl
{
    private AiSettingsViewModel? _settings;

    public AiSettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as MainViewModel)?.AiSettings);
    }

    private void Attach(AiSettingsViewModel? settings)
    {
        if (_settings is not null)
        {
            _settings.KeyDraftCleared -= ClearKey;
        }

        _settings = settings;
        if (_settings is not null)
        {
            _settings.KeyDraftCleared += ClearKey;
        }
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        KeyPlaceholder.Visibility = KeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settings is not null)
        {
            _settings.KeyDraft = KeyBox.Password;
        }
    }

    private void ClearKey()
    {
        if (KeyBox.Password.Length > 0)
        {
            KeyBox.Clear();
        }
    }
}
