namespace ChatServer.Services;

/// <summary>
/// Decides how a search term is matched. FULLTEXT is the fast path, but InnoDB will not
/// index tokens shorter than <c>innodb_ft_min_token_size</c> (3 by default) and drops
/// stopwords, so short queries would silently return nothing. Those fall back to LIKE.
/// </summary>
public static class MessageSearchQuery
{
    public const int MinFullTextTokenLength = 3;

    public static bool UseFullText(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Any(token => token.Length >= MinFullTextTokenLength);

    /// <summary>
    /// Escapes a term for use inside a LIKE pattern so wildcards typed by the user are
    /// matched literally. Backslash first, or it would double-escape the others.
    /// </summary>
    public static string EscapeLike(string query) => query
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    public static string LikePattern(string query) => $"%{EscapeLike(query)}%";
}
