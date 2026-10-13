namespace Mithu.Core.Interfaces;

/// <summary>Screenshots kept with chats (files next to the database). Content is the user's: never log it.</summary>
public interface IImageStore
{
    /// <summary>Saves PNG bytes and returns their id.</summary>
    string Save(byte[] png);

    /// <summary>The bytes, or null if the image is gone.</summary>
    byte[]? Load(string id);

    /// <summary>Full path (to open it in the Photos app), or null if it's gone.</summary>
    string? PathOf(string id);

    void Delete(IEnumerable<string> ids);

    void DeleteAll();
}

/// <summary>Reads text in an image on this PC (no AI, nothing sent anywhere).</summary>
public interface ITextRecognizer
{
    /// <summary>The text, line by line; empty when none was found. Throws <see cref="InvalidOperationException"/> if OCR isn't available.</summary>
    Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default);
}
