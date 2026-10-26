using Shared.Models;

namespace ChatServer.Data.Entities;

/// <summary>A custom emoji or sticker shared by everyone on the server.</summary>
public class CustomAssetEntity
{
    public Guid Id { get; set; }
    public CustomAssetKind Kind { get; set; }

    /// <summary>Regex-constrained to lowercase ASCII, so the default collation is fine here.</summary>
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
    public Guid UploaderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
