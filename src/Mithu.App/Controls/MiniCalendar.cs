using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Mithu.App.Controls;

/// <summary>
/// A small month grid in Mithu's dark style (the stock WPF Calendar is light-themed). ‹ October 2026 ›, then
/// Monday-first weeks; days before <see cref="Earliest"/> are dimmed and can't be picked. Arrow keys work through
/// the day buttons' normal tab order; Enter or a click picks a day.
/// </summary>
public sealed class MiniCalendar : Border
{
    private readonly TextBlock _monthText = new();
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    private DateTime _month;

    public MiniCalendar()
    {
        Padding = new Thickness(8);
        CornerRadius = new CornerRadius(14);
        Width = 7 * 32 + 16;

        var previous = NavButton("", "Previous month", -1);
        var next = NavButton("", "Next month", 1);
        _monthText.HorizontalAlignment = HorizontalAlignment.Center;
        _monthText.VerticalAlignment = VerticalAlignment.Center;
        _monthText.FontSize = 13;
        _monthText.FontWeight = FontWeights.SemiBold;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        DockPanel.SetDock(previous, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        header.Children.Add(previous);
        header.Children.Add(next);
        header.Children.Add(_monthText);

        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        var culture = CultureInfo.CurrentCulture;
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)((i + 1) % 7);
            weekdays.Children.Add(new TextBlock
            {
                Text = culture.DateTimeFormat.GetShortestDayName(day),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.6,
            });
        }

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(weekdays);
        stack.Children.Add(_days);
        Child = stack;

        Loaded += (_, _) =>
        {
            Background ??= (Brush)FindResource("MenuSurfaceBrush");
            BorderBrush ??= (Brush)FindResource("IslandRimBrush");
            BorderThickness = new Thickness(1);
            System.Windows.Documents.TextElement.SetForeground(this, (Brush)FindResource("TextBrush"));
            Build();
        };
    }

    /// <summary>A day was picked.</summary>
    public event Action<DateTime>? DatePicked;

    public DateTime SelectedDate { get; set; } = DateTime.Today;

    /// <summary>The first day that can be picked (today).</summary>
    public DateTime Earliest { get; set; } = DateTime.Today;

    /// <summary>Shows the month of <paramref name="date"/> with it selected.</summary>
    public void ShowDate(DateTime date)
    {
        SelectedDate = date.Date;
        _month = new DateTime(date.Year, date.Month, 1);
        if (IsLoaded)
        {
            Build();
        }
    }

    private void Build()
    {
        if (_month == default)
        {
            _month = new DateTime(SelectedDate.Year, SelectedDate.Month, 1);
        }

        _monthText.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _days.Children.Clear();
        var offset = ((int)_month.DayOfWeek + 6) % 7; // Monday first
        var first = _month.AddDays(-offset);
        for (var i = 0; i < 42; i++)
        {
            _days.Children.Add(DayButton(first.AddDays(i)));
        }
    }

    private Button DayButton(DateTime date)
    {
        var inMonth = date.Month == _month.Month;
        var selected = date == SelectedDate.Date;
        var today = date == DateTime.Today;
        var button = new Button
        {
            Content = date.Day.ToString(CultureInfo.CurrentCulture),
            Style = (Style)FindResource("IslandIconButton"),
            Width = 30,
            Height = 28,
            FontSize = 12,
            FontFamily = Themes.AppFonts.Ui,
            IsEnabled = date >= Earliest.Date,
            Opacity = date < Earliest.Date ? 0.25 : inMonth ? 1 : 0.45,
            ToolTip = null,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, date.ToString("D", CultureInfo.CurrentCulture));

        if (selected)
        {
            button.Background = (Brush)FindResource("CyanSoftBrush");
            button.Foreground = (Brush)FindResource("CyanBrush");
            button.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            button.Foreground = (Brush)FindResource(today ? "CyanBrush" : "TextBrush");
        }

        button.Click += (_, _) =>
        {
            SelectedDate = date;
            DatePicked?.Invoke(date);
        };
        return button;
    }

    private Button NavButton(string glyph, string name, int months)
    {
        var button = new Button { Content = glyph, Width = 28, Height = 28, FontSize = 10 };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Loaded += (_, _) => button.Style ??= (Style)FindResource("IslandIconButton");
        button.Click += (_, _) =>
        {
            _month = _month.AddMonths(months);
            Build();
        };
        return button;
    }
}
