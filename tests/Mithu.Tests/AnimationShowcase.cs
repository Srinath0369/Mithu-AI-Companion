using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mithu.App.Controls;
using Mithu.Core.Models;

namespace Mithu.Tests;

/// <summary>
/// Plays every face animation in a window, one after another with a caption, and saves the frames (15 fps) so they
/// can be looked at frame by frame. Opt-in: runs only with MITHU_SHOWCASE set to an output folder, e.g.
/// <c>$env:MITHU_SHOWCASE = "C:\temp\showcase"; dotnet test --filter AnimationShowcase</c>.
/// </summary>
public sealed class AnimationShowcase
{
    private const int Fps = 15;

    [Fact]
    public void Plays_every_animation()
    {
        if (Environment.GetEnvironmentVariable("MITHU_SHOWCASE") is not { Length: > 0 } outDir)
        {
            return; // opt-in (it opens a window and takes about a minute)
        }

        Sta(() => Play(outDir));
    }

    private static void Play(string outDir)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/{typeof(CompanionFaceControl).Assembly.GetName().Name};component/Themes/Colors.xaml") });

        var face = new CompanionFaceControl { Width = 216, Height = 174 };
        var caption = new TextBlock { Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        var detail = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var stage = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x1A)), Width = 300, Height = 270 };
        stage.Children.Add(new Border { Height = 18 });
        stage.Children.Add(face);
        stage.Children.Add(caption);
        stage.Children.Add(detail);

        var work = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = "Mithu animations",
            Content = stage,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            Topmost = true,
            Left = work.Right - 340,
            Top = work.Bottom - 330,
        };
        window.Show();
        Directory.CreateDirectory(outDir);

        var index = 0;
        var manifest = new List<string>();

        void Record(string name, string title, string about, int ms, params (int At, Action Do)[] steps)
        {
            index++;
            caption.Text = title;
            detail.Text = about;
            face.Mood = FaceMood.Idle;
            Wait(250); // settle on the calm face first

            var dir = Path.Combine(outDir, $"{index:00}-{name}");
            Directory.CreateDirectory(dir);
            manifest.Add($"{index:00}-{name}\t{title}\t{about}");
            var pending = new Queue<(int At, Action Do)>(steps.OrderBy(s => s.At));
            var clock = Stopwatch.StartNew();
            for (var frame = 0; clock.ElapsedMilliseconds < ms; frame++)
            {
                while (pending.Count > 0 && pending.Peek().At <= clock.ElapsedMilliseconds)
                {
                    pending.Dequeue().Do();
                }

                Save(stage, Path.Combine(dir, $"{frame:000}.png"));
                var next = (frame + 1) * 1000 / Fps;
                Wait(Math.Max(1, next - (int)clock.ElapsedMilliseconds));
            }
        }

        Action Mood(FaceMood mood) => () => face.Mood = mood;

        Record("idle", "Idle", "Default face · blinks every 3–7 s", 2600,
            (500, face.Blink), (1500, face.Blink), (1780, face.Blink));
        Record("hover", "Hover", "Mouse over the face", 2200, (400, Mood(FaceMood.Hover)), (1700, Mood(FaceMood.Idle)));
        Record("listening", "Listening", "Waiting for your answer", 2400, (400, Mood(FaceMood.Listening)), (1900, Mood(FaceMood.Idle)));
        Record("thinking", "Thinking", "Eyes up and pondering · thought dots", 5200,
            (400, Mood(FaceMood.Thinking)), (1900, face.Ponder), (3300, face.Ponder), (4600, Mood(FaceMood.Idle)));
        Record("happy", "Happy", "A click, a saved note, a finished task", 2400, (400, Mood(FaceMood.Happy)), (1900, Mood(FaceMood.Idle)));
        Record("celebrating", "Celebrating", "Focus round done · happy hop", 2600, (400, Mood(FaceMood.Celebrating)), (2100, Mood(FaceMood.Idle)));
        Record("concerned", "Concerned", "Something went wrong", 2400, (400, Mood(FaceMood.Concerned)), (1900, Mood(FaceMood.Idle)));
        Record("focused", "Focused", "Lids lowered, eyes on the work · still", 3400, (400, Mood(FaceMood.Focused)), (1500, face.Blink), (2800, Mood(FaceMood.Idle)));
        Record("relaxed", "Relaxed", "Focus break", 2400, (400, Mood(FaceMood.Relaxed)), (1900, Mood(FaceMood.Idle)));
        Record("hurt", "Hurt + punch", "Clicking the face quickly", 2600,
            (400, Mood(FaceMood.Hurt)), (400, face.Punch), (750, face.Punch), (1100, face.Punch), (1450, face.Punch), (2100, Mood(FaceMood.Idle)));
        Record("angry", "Angry", "After the punching · smooth in and out", 3800, (400, Mood(FaceMood.Angry)), (2700, Mood(FaceMood.Idle)));
        Record("sleepy", "Sleepy", "You're away · breathing, z's drifting up", 7000, (400, Mood(FaceMood.Sleepy)), (6400, Mood(FaceMood.Idle)));

        File.WriteAllLines(Path.Combine(outDir, "manifest.tsv"), manifest);
        window.Close();
    }

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Lets the dispatcher (and the animations) run for a while.</summary>
    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Sta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
