using MagicOnion;
using Shared.Models;
using Shared.Requests;

namespace Shared.Services;

/// <summary>
/// Server-wide custom emoji and stickers. Any member may add; the uploader or a
/// Moderator+ may remove.
/// </summary>
public interface IServerAssetService : IService<IServerAssetService>
{
    UnaryResult<CustomAssetInfo[]> GetAssetsAsync();
    UnaryResult<CustomAssetInfo> CreateAssetAsync(CreateCustomAssetRequest request);
    UnaryResult<bool> DeleteAssetAsync(Guid assetId);
}
