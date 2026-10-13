using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mithu.App.Vision;
using Mithu.Core.Ai;
using Mithu.Core.Vision;
using Mithu.Infrastructure.Ai;
using Mithu.Infrastructure.Vision;

namespace Mithu.Tests;

/// <summary>Small PNGs for tests.</summary>
internal static class TestImages
{
    public static BitmapSource Bitmap(int width, int height, byte shade = 0x40)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = shade;
            pixels[i + 1] = (byte)(i / 4 % 256);
            pixels[i + 2] = 0x80;
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] Png(int width, int height, byte shade = 0x40)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(width, height, shade)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static (int Width, int Height) SizeOf(byte[] png)
    {
        var frame = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}

public class CaptureGeometryTests
{
    private static readonly CaptureRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void A_drag_in_any_direction_gives_the_same_area_clamped_to_the_screen()
    {
        Assert.Equal(new CaptureRect(100, 50, 200, 150), CaptureGeometry.FromDrag(100, 50, 300, 200, Screen));
        Assert.Equal(new CaptureRect(100, 50, 200, 150), CaptureGeometry.FromDrag(300, 200, 100, 50, Screen));
        Assert.Equal(new CaptureRect(1800, 1000, 120, 80), CaptureGeometry.FromDrag(1800, 1000, 2500, 1300, Screen));
    }

    [Theory]
    [InlineData(10, 10, 15, 14, false)] // a wobbly click
    [InlineData(10, 10, 16, 10, true)]
    [InlineData(10, 10, 10, 4, true)]
    public void Small_movements_are_clicks(int x1, int y1, int x2, int y2, bool isDrag) =>
        Assert.Equal(isDrag, CaptureGeometry.IsDrag(x1, y1, x2, y2));

    [Fact]
    public void A_click_takes_the_front_window_under_the_mouse_clipped_to_the_screen()
    {
        var front = new CaptureRect(200, 200, 400, 300);
        var back = new CaptureRect(-100, 0, 1200, 900); // hangs off the left edge
        var windows = new[] { front, back };

        Assert.Equal(front, CaptureGeometry.WindowAt(300, 300, windows, Screen));
        Assert.Equal(new CaptureRect(0, 0, 1100, 900), CaptureGeometry.WindowAt(50, 50, windows, Screen));
        Assert.Null(CaptureGeometry.WindowAt(1500, 1000, windows, Screen)); // the desktop: whole screen instead
    }

    [Fact]
    public void Big_screenshots_are_scaled_for_the_ai_and_small_ones_are_not()
    {
        Assert.Equal((1568, 882), CaptureGeometry.FitForAi(3840, 2160));
        Assert.Equal((551, 1568), CaptureGeometry.FitForAi(1080, 3072));
        Assert.Equal((800, 600), CaptureGeometry.FitForAi(800, 600));
    }
}

public class ChatImageTests
{
    [Fact]
    public void Images_are_saved_by_id_without_their_bytes()
    {
        var message = AiMessage.UserWithImages("what's this?", [new AiImage("abc", "image/png", [1, 2, 3])]);
        var json = AiMessageJson.Write(message.Parts);
        Assert.DoesNotContain("AQID", json, StringComparison.Ordinal); // base64 of 1,2,3: bytes live in the image store

        var parts = AiMessageJson.Read(json);
        var image = Assert.IsType<AiImage>(parts[0]);
        Assert.Equal(("abc", "image/png"), (image.Id, image.MediaType));
        Assert.Null(image.Data);
        Assert.Equal("what's this?", Assert.IsType<AiText>(parts[1]).Text);
    }

    [Fact]
    public void Only_the_newest_screenshots_are_resent()
    {
        var history = Enumerable.Range(0, 5)
            .SelectMany(i => new[] { AiMessage.UserWithImages($"q{i}", [new AiImage($"img{i}")]), AiMessage.Assistant($"a{i}") })
            .ToList();

        var limited = ChatRules.LimitImages(history);

        Assert.Equal(["img2", "img3", "img4"], limited.SelectMany(m => m.Images).Select(i => i.Id));
        Assert.Contains("earlier screenshot", limited[0].Text);
        Assert.Equal("q4", limited[8].Text);
    }

    [Fact]
    public void Merged_user_messages_keep_their_screenshots()
    {
        var history = new[]
        {
            AiMessage.UserWithImages("look", [new AiImage("one")]),
            AiMessage.User("well?"), // the first request failed
        };

        var message = Assert.Single(ChatRules.Context(history));
        Assert.Equal("one", Assert.Single(message.Images).Id);
        Assert.Contains("look", message.Text);
        Assert.Contains("well?", message.Text);
    }

    [Fact]
    public void Openai_compatible_requests_carry_images_as_data_urls()
    {
        var message = AiMessage.UserWithImages("what's this?", [new AiImage("x", "image/png", [1, 2, 3])]);
        var body = OpenAiCompatibleClient.BuildBody(new AiRequest(string.Empty, [message]), "m");

        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("text", (string?)content[0]!["type"]);
        Assert.Equal("what's this?", (string?)content[0]!["text"]);
        Assert.Equal("image_url", (string?)content[1]!["type"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)content[1]!["image_url"]!["url"]);
    }

    [Fact]
    public void A_screenshot_mithu_took_follows_its_tool_result()
    {
        var results = new AiMessage(AiRole.User, [new AiToolResult("s1", "Screenshot attached."), new AiImage("x", "image/png", [1, 2, 3])]);
        var body = OpenAiCompatibleClient.BuildBody(new AiRequest(string.Empty, [results]), "m");

        var messages = body["messages"]!.AsArray();
        Assert.Equal("tool", (string?)messages[0]!["role"]);
        Assert.Equal("user", (string?)messages[1]!["role"]);
        Assert.Equal("image_url", (string?)messages[1]!["content"]![0]!["type"]);
    }
}

public sealed class FileImageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Saves_loads_and_deletes_screenshots()
    {
        var store = new FileImageStore(_dir);
        var png = TestImages.Png(4, 3);

        var id = store.Save(png);
        Assert.Equal(png, store.Load(id));
        Assert.True(File.Exists(store.PathOf(id)));

        var other = store.Save(TestImages.Png(2, 2));
        store.Delete([id]);
        Assert.Null(store.Load(id));
        Assert.Null(store.PathOf(id));
        Assert.NotNull(store.Load(other));

        store.DeleteAll();
        Assert.Null(store.Load(other));
    }

    [Theory]
    [InlineData("..\\secrets")]
    [InlineData("../../mithu.db")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("")]
    public void Ids_cannot_reach_outside_the_folder(string id)
    {
        var store = new FileImageStore(_dir);
        Assert.Null(store.Load(id));
        Assert.Null(store.PathOf(id));
        store.Delete([id]); // no-op, no throw
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public class ScreenCaptureTests
{
    [Fact]
    public void Crops_the_pick_and_scales_large_shots_for_the_ai()
    {
        var snapshot = new ScreenSnapshot(TestImages.Bitmap(2000, 1000), new CaptureRect(0, 0, 2000, 1000), []);

        var png = ScreenCapture.Crop(snapshot, new CaptureRect(100, 100, 300, 200));
        Assert.Equal((300, 200), TestImages.SizeOf(png));

        var whole = ScreenCapture.Crop(snapshot, new CaptureRect(0, 0, 2000, 1000));
        Assert.Equal((1568, 784), TestImages.SizeOf(ScreenCapture.ForAi(whole)));
        Assert.Same(png, ScreenCapture.ForAi(png)); // small enough already
    }
}
