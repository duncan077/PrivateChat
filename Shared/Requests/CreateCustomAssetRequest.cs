using MessagePack;
using Shared.Models;

namespace Shared.Requests;

[MessagePackObject]
public class CreateCustomAssetRequest
{
    [Key(0)] public CustomAssetKind Kind { get; set; }
    [Key(1)] public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Must be a URL returned by the upload endpoint — the server verifies it points at
    /// a file it actually stores rather than an arbitrary remote host.
    /// </summary>
    [Key(2)] public string Url { get; set; } = string.Empty;
}
