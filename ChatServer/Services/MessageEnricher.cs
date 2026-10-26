using ChatServer.Data;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Models;

namespace ChatServer.Services;

/// <summary>
/// Fills in the parts of a message page that don't come from the message row itself.
/// One extra query per page rather than a join, so the main projection stays a clean
/// index range scan and reactions don't multiply the rows it returns.
/// </summary>
public static class MessageEnricher
{
    public static async Task AttachReactionsAsync(AppDbContext db, ChatMessage[] messages, CancellationToken ct = default)
    {
        if (messages.Length == 0) return;

        var ids = messages.Select(m => m.Id).ToArray();
        var rows = await db.Reactions
            .AsNoTracking()
            .Where(r => r.MessageId != null && ids.Contains(r.MessageId.Value))
            .Select(r => new { MessageId = r.MessageId!.Value, r.Emoji, r.UserId })
            .ToArrayAsync(ct);

        if (rows.Length == 0) return;

        var byMessage = rows
            .GroupBy(r => r.MessageId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.Emoji, StringComparer.Ordinal)
                      .Select(e => new ReactionInfo { Emoji = e.Key, UserIds = e.Select(r => r.UserId).ToArray() })
                      .ToArray());

        foreach (var message in messages)
        {
            if (byMessage.TryGetValue(message.Id, out var reactions))
            {
                message.Reactions = reactions;
            }
        }
    }

    /// <summary>
    /// Builds each reply's quoted preview from the parent as it stands now, so an edited
    /// parent quotes truthfully and a deleted one reads as unavailable. A parent that is
    /// missing entirely (hard-deleted room message, or an FK nulled by SET NULL) lands in
    /// the same unavailable state.
    /// </summary>
    public static async Task AttachReplyPreviewsAsync(AppDbContext db, ChatMessage[] messages, CancellationToken ct = default)
    {
        var parentIds = messages
            .Where(m => m.ReplyToMessageId != null)
            .Select(m => m.ReplyToMessageId!.Value)
            .Distinct()
            .ToArray();

        if (parentIds.Length == 0) return;

        var parents = await db.Messages
            .AsNoTracking()
            .Where(m => parentIds.Contains(m.Id))
            .Select(m => new { m.Id, SenderName = m.Sender.UserName!, m.Content, m.IsDeleted })
            .ToDictionaryAsync(m => m.Id, ct);

        foreach (var message in messages)
        {
            if (message.ReplyToMessageId is not { } parentId) continue;

            message.ReplyPreview = parents.TryGetValue(parentId, out var parent) && !parent.IsDeleted
                ? new MessageReplyPreview { SenderName = parent.SenderName, Snippet = Snippet(parent.Content) }
                : new MessageReplyPreview { IsUnavailable = true };
        }
    }

    public static async Task AttachReplyPreviewsAsync(AppDbContext db, PrivateMessage[] messages, CancellationToken ct = default)
    {
        var parentIds = messages
            .Where(m => m.ReplyToMessageId != null)
            .Select(m => m.ReplyToMessageId!.Value)
            .Distinct()
            .ToArray();

        if (parentIds.Length == 0) return;

        var parents = await db.PrivateMessages
            .AsNoTracking()
            .Where(m => parentIds.Contains(m.Id))
            .Select(m => new { m.Id, SenderName = m.Sender.UserName!, m.Content, m.IsDeleted })
            .ToDictionaryAsync(m => m.Id, ct);

        foreach (var message in messages)
        {
            if (message.ReplyToMessageId is not { } parentId) continue;

            message.ReplyPreview = parents.TryGetValue(parentId, out var parent) && !parent.IsDeleted
                ? new MessageReplyPreview { SenderName = parent.SenderName, Snippet = Snippet(parent.Content) }
                : new MessageReplyPreview { IsUnavailable = true };
        }
    }

    /// <summary>
    /// Raw leading characters of the parent — the quote is rendered as plain text, so
    /// truncating markdown mid-construct is harmless.
    /// </summary>
    public static string Snippet(string? content)
    {
        var text = (content ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= ChatLimits.ReplySnippetLength
            ? text
            : text[..ChatLimits.ReplySnippetLength] + "…";
    }

    public static async Task AttachReactionsAsync(AppDbContext db, PrivateMessage[] messages, CancellationToken ct = default)
    {
        if (messages.Length == 0) return;

        var ids = messages.Select(m => m.Id).ToArray();
        var rows = await db.Reactions
            .AsNoTracking()
            .Where(r => r.PrivateMessageId != null && ids.Contains(r.PrivateMessageId.Value))
            .Select(r => new { MessageId = r.PrivateMessageId!.Value, r.Emoji, r.UserId })
            .ToArrayAsync(ct);

        if (rows.Length == 0) return;

        var byMessage = rows
            .GroupBy(r => r.MessageId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.Emoji, StringComparer.Ordinal)
                      .Select(e => new ReactionInfo { Emoji = e.Key, UserIds = e.Select(r => r.UserId).ToArray() })
                      .ToArray());

        foreach (var message in messages)
        {
            if (byMessage.TryGetValue(message.Id, out var reactions))
            {
                message.Reactions = reactions;
            }
        }
    }
}
