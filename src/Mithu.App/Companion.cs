using System.ComponentModel;
using Mithu.Core.Models;

namespace Mithu.App;

/// <summary>
/// The companion's name as the user chose it ("Mithu" by default), for everything on screen that talks about the
/// companion: XAML binds to it with <c>{Binding Path=(app:Companion.Name)}</c> and updates when it changes. The
/// app's own name (Start menu, installer, updates) stays "Mithu".
/// </summary>
public static class Companion
{
    private static string _name = CompanionNames.Default;

    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    public static string Name
    {
        get => _name;
        set
        {
            var clean = CompanionNames.Clean(value);
            if (clean == _name)
            {
                return;
            }

            _name = clean;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
}
