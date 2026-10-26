using Microsoft.Extensions.Caching.Distributed;

namespace ChatServer.Services;

/// <summary>
/// The distributed-cache key space, in one place.
///
/// Keys are built here rather than at each call site because the previous scheme
/// drifted: room history was written under a key carrying the caller's page size and
/// cursor, while invalidation only ever removed a single hardcoded shape, so most
/// entries were unreachable once written. Every key below is derived solely from
/// server-side state, and every writer has a matching remover.
/// </summary>
internal static class CacheKeys
{
    /// <summary>Page size the shipped clients request, and the only one cached.</summary>
    public const int DefaultMessagePageSize = 50;

    /// <summary>Upper bound on a client-supplied page size.</summary>
    public const int MaxMessagePageSize = 100;

    /// <summary>Column bounds enforced at the writers, mirroring AppDbContext.OnModelCreating.</summary>
    public const int MaxRoomNameLength = 256;
    public const int MaxRoomDescriptionLength = 1024;

    public const string Rooms = "rooms:all";
    public const string ServerMembers = "members:all";

    public static string RoomMessages(Guid roomId) => $"messages:{roomId}";

    /// <summary>
    /// Bumped by every directory invalidation.
    ///
    /// A reader that missed the cache holds a result from before a concurrent write, and
    /// if it stores that result after the invalidation it pins pre-mutation data for the
    /// full TTL. Readers capture this counter before querying and skip their write if it
    /// moved, so a stale snapshot loses the race instead of winning it.
    /// </summary>
    private static long _directoryGeneration;

    public static long DirectoryGeneration => Interlocked.Read(ref _directoryGeneration);

    /// <summary>
    /// Drops both directory views. Room metadata and room membership each feed the
    /// room list and the member list (via <c>ServerMemberInfo.RoomIds</c>), so the two
    /// always expire together - keeping them separate is how they fall out of step.
    /// </summary>
    public static async Task InvalidateDirectoryAsync(IDistributedCache cache)
    {
        Interlocked.Increment(ref _directoryGeneration);
        await cache.RemoveAsync(Rooms);
        await cache.RemoveAsync(ServerMembers);
    }

    /// <summary>Invalidates just the member directory (profile and role edits).</summary>
    public static async Task InvalidateServerMembersAsync(IDistributedCache cache)
    {
        Interlocked.Increment(ref _directoryGeneration);
        await cache.RemoveAsync(ServerMembers);
    }
}
