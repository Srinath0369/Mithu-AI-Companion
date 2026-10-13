using System.Text;
using System.Text.RegularExpressions;

namespace Mithu.Core.Ai;

/// <summary>A reaction the AI can give the companion's face with a tag at the start of its reply.</summary>
public enum FaceReaction
{
    Happy,
    Celebrate,
    Wink,
    Surprised,
    Ouch,
    Hmph,
    Sleepy,
    Thinking,
    Concerned,
}

/// <summary>
/// The AI may start a reply with one hidden tag, e.g. <c>[face:wink]</c>, to make the face react (owner, 2026-10-13:
/// "answer in a witty way and use the animation sometimes"). The tag is never shown or saved: the stream is filtered
/// (<see cref="FaceTagFilter"/>) and the finished message stripped (<see cref="Strip(AiMessage, out FaceReaction?)"/>).
/// Only a tag at the very start counts. Pure; unit-tested.
/// </summary>
public static partial class FaceTags
{
    /// <summary>The names the AI may use, as written in the tag.</summary>
    public static readonly IReadOnlyList<string> Names = Enum.GetNames<FaceReaction>().Select(n => n.ToLowerInvariant()).ToList();

    [GeneratedRegex(@"^\s*\[face:\s*([A-Za-z]+)\s*\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingTag();

    public static bool TryParse(string name, out FaceReaction reaction) =>
        Enum.TryParse(name.Trim(), ignoreCase: true, out reaction) && Enum.IsDefined(reaction);

    /// <summary>The text without a leading tag; <paramref name="reaction"/> is the face it asked for (null: none or unknown).</summary>
    public static string Strip(string text, out FaceReaction? reaction)
    {
        reaction = null;
        var match = LeadingTag().Match(text);
        if (!match.Success)
        {
            return text;
        }

        if (TryParse(match.Groups[1].Value, out var face))
        {
            reaction = face;
        }

        return text[match.Length..];
    }

    /// <summary>The message with the tag taken out of its first text (everything else, e.g. thinking or tool calls, unchanged).</summary>
    public static AiMessage Strip(AiMessage message, out FaceReaction? reaction)
    {
        reaction = null;
        var parts = message.Parts.ToList();
        var first = parts.FindIndex(p => p is AiText { Text.Length: > 0 });
        if (first < 0)
        {
            return message;
        }

        var text = ((AiText)parts[first]).Text;
        var clean = Strip(text, out reaction);
        if (clean.Length == text.Length)
        {
            return message;
        }

        parts[first] = new AiText(clean);
        return message with { Parts = parts };
    }
}

/// <summary>
/// Takes a leading face tag out of a streamed reply. The first few characters are held back only while they could
/// still be the start of a tag ("[fa…"), so the user never sees a tag flash by; anything else passes straight through.
/// </summary>
public sealed class FaceTagFilter
{
    private const string Opening = "[face:";
    private const int Longest = 32;

    private readonly StringBuilder _held = new();
    private bool _decided;
    private bool _trimSpace; // just after a tag: the space before the words is dropped, whichever piece it comes in

    /// <summary>Text to show for this piece (may be empty while held), and the face asked for, once.</summary>
    public (string Text, FaceReaction? Reaction) Push(string piece)
    {
        if (_decided)
        {
            if (_trimSpace)
            {
                piece = piece.TrimStart();
                _trimSpace = piece.Length == 0;
            }

            return (piece, null);
        }

        _held.Append(piece);
        var held = _held.ToString();
        var start = held.TrimStart();
        if (start.Length == 0)
        {
            return (string.Empty, null); // only spaces so far
        }

        var couldBeTag = start.Length <= Opening.Length
            ? Opening.StartsWith(start, StringComparison.OrdinalIgnoreCase)
            : start.StartsWith(Opening, StringComparison.OrdinalIgnoreCase);
        if (!couldBeTag)
        {
            return Release(held, null);
        }

        if (start.Contains(']', StringComparison.Ordinal))
        {
            var rest = FaceTags.Strip(held, out var reaction);
            _trimSpace = rest.Length == 0;
            return Release(rest, reaction);
        }

        // A "[face:" that never closes isn't a tag: show it as it is.
        return start.Length > Longest ? Release(held, null) : (string.Empty, null);
    }

    /// <summary>The reply ended: anything still held back is shown as it is.</summary>
    public string Flush()
    {
        var held = _decided ? string.Empty : _held.ToString();
        _decided = true;
        _held.Clear();
        return held;
    }

    /// <summary>A new reply starts.</summary>
    public void Reset()
    {
        _decided = false;
        _trimSpace = false;
        _held.Clear();
    }

    private (string, FaceReaction?) Release(string text, FaceReaction? reaction)
    {
        _decided = true;
        _held.Clear();
        return (text, reaction);
    }
}
