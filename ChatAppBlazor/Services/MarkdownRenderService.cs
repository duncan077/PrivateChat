using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace ChatAppBlazor.Services;

/// <summary>
/// Renders chat message content to HTML. Raw HTML in the source is disabled at the
/// pipeline level, so every tag in the output comes from a markdown construct or from
/// this class; link destinations are restricted to http/https/mailto because markdown
/// would otherwise happily emit javascript: hrefs.
/// </summary>
public sealed partial class MarkdownRenderService
{
    // Mirrors MentionTokenRegex in ChatRooms.razor so a token that notifies is the
    // same token that gets highlighted.
    [GeneratedRegex(@"(^|\s)@([A-Za-z0-9_.-]+)")]
    private static partial Regex MentionTokenRegex();

    // Must agree with the server's ServerAssetServiceImpl name rule.
    [GeneratedRegex(":([a-z0-9_]{2,24}):")]
    private static partial Regex CustomEmojiTokenRegex();

    private const int MaxCacheEntries = 600;

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private readonly ConcurrentDictionary<Guid, (string Source, int Registry, MarkupString Html)> _cache = new();

    /// <summary>
    /// Resolves a custom-emoji name to its image URL, or null when unknown. Supplied by
    /// the caller so this service stays free of state-store dependencies.
    /// </summary>
    public Func<string, string?>? ResolveCustomEmoji { get; set; }

    /// <summary>
    /// Changes whenever the custom-emoji registry does, so cached HTML that rendered a
    /// then-unknown token as plain text is re-rendered once the emoji exists.
    /// </summary>
    public int RegistryVersion { get; set; }

    public MarkupString Render(Guid messageId, string? content, Func<string, bool>? isMentionName = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        // Keyed by message id with the source text as the staleness check, so edits
        // re-render without needing a separate version field on the model.
        if (_cache.TryGetValue(messageId, out var hit) &&
            string.Equals(hit.Source, content, StringComparison.Ordinal) &&
            hit.Registry == RegistryVersion)
        {
            return hit.Html;
        }

        var html = RenderCore(content, isMentionName);

        // The chat holds at most a few hundred rendered messages at a time; a full
        // reset on overflow is simpler than LRU bookkeeping and re-rendering is cheap.
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[messageId] = (content, RegistryVersion, html);
        return html;
    }

    private MarkupString RenderCore(string content, Func<string, bool>? isMentionName)
    {
        var document = Markdown.Parse(content, _pipeline);
        ApplyLinkPolicy(document);
        WrapMentions(document, isMentionName);
        ReplaceCustomEmoji(document, IsJumboCandidate(content));

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return new MarkupString(writer.ToString());
    }

    private static void ApplyLinkPolicy(MarkdownDocument document)
    {
        foreach (var link in document.Descendants().OfType<LinkInline>().ToList())
        {
            var url = link.Url ?? string.Empty;
            var allowed = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                          (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));

            if (!allowed)
            {
                var label = GetPlainText(link);
                link.ReplaceBy(new LiteralInline(label.Length > 0 ? label : url));
                continue;
            }

            // Image syntax would hotlink arbitrary remote content into the chat;
            // demote it to a plain link and let the existing preview card handle it.
            link.IsImage = false;

            var attributes = link.GetAttributes();
            attributes.AddProperty("target", "_blank");
            attributes.AddProperty("rel", "noopener noreferrer");
        }
    }

    private static void WrapMentions(MarkdownDocument document, Func<string, bool>? isMentionName)
    {
        if (isMentionName == null)
        {
            return;
        }

        // CodeInline/FencedCodeBlock content is not LiteralInline, so code is
        // naturally excluded from mention styling.
        foreach (var literal in document.Descendants().OfType<LiteralInline>().ToList())
        {
            var text = literal.Content.ToString();
            var matches = MentionTokenRegex().Matches(text);
            if (matches.Count == 0)
            {
                continue;
            }

            Inline anchor = literal;
            void Append(Inline node)
            {
                anchor.InsertAfter(node);
                anchor = node;
            }

            var position = 0;
            var replaced = false;
            foreach (Match match in matches)
            {
                var name = match.Groups[2].Value;
                if (!isMentionName(name))
                {
                    continue;
                }

                var tokenStart = match.Groups[2].Index - 1; // include the '@'
                if (tokenStart > position)
                {
                    Append(new LiteralInline(text[position..tokenStart]));
                }

                // Tags are constants; the name itself goes through a literal so the
                // HTML renderer escapes it.
                Append(new HtmlInline("<span class=\"chat-mention\">"));
                Append(new LiteralInline("@" + name));
                Append(new HtmlInline("</span>"));
                position = match.Groups[2].Index + name.Length;
                replaced = true;
            }

            if (!replaced)
            {
                continue;
            }

            if (position < text.Length)
            {
                Append(new LiteralInline(text[position..]));
            }

            literal.Remove();
        }
    }

    /// <summary>
    /// A message that is nothing but custom-emoji tokens renders them large, the way
    /// chat apps do for an all-emoji message.
    /// </summary>
    private static bool IsJumboCandidate(string content)
    {
        var stripped = CustomEmojiTokenRegex().Replace(content, string.Empty).Trim();
        return stripped.Length == 0 && CustomEmojiTokenRegex().IsMatch(content);
    }

    private void ReplaceCustomEmoji(MarkdownDocument document, bool jumbo)
    {
        if (ResolveCustomEmoji == null) return;

        foreach (var literal in document.Descendants().OfType<LiteralInline>().ToList())
        {
            var text = literal.Content.ToString();
            var matches = CustomEmojiTokenRegex().Matches(text);
            if (matches.Count == 0) continue;

            Inline anchor = literal;
            void Append(Inline node)
            {
                anchor.InsertAfter(node);
                anchor = node;
            }

            var position = 0;
            var replaced = false;
            foreach (Match match in matches)
            {
                var name = match.Groups[1].Value;
                var url = ResolveCustomEmoji(name);
                // Unknown names stay literal text — a deleted emoji degrades to :name:
                // rather than a broken image.
                if (url == null) continue;

                if (match.Index > position)
                {
                    Append(new LiteralInline(text[position..match.Index]));
                }

                var cssClass = jumbo ? "chat-custom-emoji chat-custom-emoji-jumbo" : "chat-custom-emoji";
                // Url comes from the server's own upload path, and name is regex-constrained.
                Append(new HtmlInline($"<img class=\"{cssClass}\" src=\"{url}\" alt=\":{name}:\" title=\":{name}:\" />"));
                position = match.Index + match.Length;
                replaced = true;
            }

            if (!replaced) continue;

            if (position < text.Length)
            {
                Append(new LiteralInline(text[position..]));
            }

            literal.Remove();
        }
    }

    private static string GetPlainText(ContainerInline container)
    {
        var parts = new List<string>();
        foreach (var child in container)
        {
            if (child is LiteralInline literal)
            {
                parts.Add(literal.Content.ToString());
            }
            else if (child is ContainerInline nested)
            {
                parts.Add(GetPlainText(nested));
            }
        }

        return string.Concat(parts);
    }
}
