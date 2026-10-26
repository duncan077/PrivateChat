namespace Shared;

/// <summary>
/// Limits shared by server-side validation and client-side UI so the two can't
/// drift apart. Server enforcement is authoritative; the client uses these to
/// fail fast and size inputs.
/// </summary>
public static class ChatLimits
{
    public const int MaxMessageLength = 8000;

    /// <summary>
    /// Max UTF-16 units for a reaction. Fits multi-codepoint emoji (ZWJ sequences,
    /// skin tones) and custom-emoji tokens of the form <c>:name:</c>.
    /// </summary>
    public const int MaxEmojiLength = 32;

    public const int MaxCustomAssetNameLength = 24;
    public const int MaxEmojiUploadBytes = 256 * 1024;
    public const int MaxStickerUploadBytes = 2 * 1024 * 1024;

    public const int ReplySnippetLength = 140;

    public const int TypingThrottleSeconds = 3;
    public const int TypingExpirySeconds = 5;

    public const int MaxSearchPageSize = 50;
}
