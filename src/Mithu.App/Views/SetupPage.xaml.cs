using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mithu.App.Views;

/// <summary>The first-run setup page (see <see cref="ViewModels.SetupViewModel"/>). DataContext = MainViewModel.</summary>
public partial class SetupPage : UserControl
{
    public SetupPage()
    {
        InitializeComponent();

        // The name step: the cursor is in the box with the name selected, so typing replaces it.
        NameBox.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Keyboard.Focus(NameBox);
                    NameBox.SelectAll();
                }, DispatcherPriority.Input);
            }
        };
    }

    /// <summary>The panel opened on this page: put the keyboard where it makes sense.</summary>
    public void FocusStep()
    {
        if (NameBox.IsVisible)
        {
            Keyboard.Focus(NameBox);
        }
        else if (NextButton.IsVisible)
        {
            Keyboard.Focus(NextButton);
        }
    }
}
