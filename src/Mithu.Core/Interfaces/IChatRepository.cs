using Mithu.Core.Ai;

namespace Mithu.Core.Interfaces;

/// <summary>Saved AI chats. Message text is user content: never log it.</summary>
public interface IChatRepository
{
    /// <summary>Chats, most recently active first; <paramref name="search"/> matches titles and message text.</summary>
    IReadOnlyList<ChatThread> ListChats(string? search = null);

    ChatThread? GetChat(string chatId);

    IReadOnlyList<ChatEntry> GetMessages(string chatId);

    void CreateChat(ChatThread chat);

    /// <summary>Adds messages in order (one transaction) and moves the chat's "last active" time.</summary>
    void AddMessages(string chatId, IReadOnlyList<AiMessage> messages, DateTimeOffset nowUtc, string? model = null);

    void RenameChat(string chatId, string title);

    void DeleteChat(string chatId);

    void DeleteAllChats();
}
