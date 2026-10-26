using System.Text.RegularExpressions;
using ChatServer.Auth;
using ChatServer.Data;
using ChatServer.Data.Entities;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Models;
using Shared.Requests;
using Shared.Services;

namespace ChatServer.Services;

[JwtAuthFilter]
public partial class ServerAssetServiceImpl : ServiceBase<IServerAssetService>, IServerAssetService
{
    // Lowercase ASCII only: the name appears inside :name: tokens in message text, so it
    // has to survive markdown, be typeable, and compare unambiguously.
    [GeneratedRegex("^[a-z0-9_]{2,24}$")]
    private static partial Regex AssetNameRegex();

    // Matches exactly what UploadController hands back.
    [GeneratedRegex(@"^/uploads/[0-9a-fA-F-]{36}\.(png|gif|webp|jpg|jpeg)$")]
    private static partial Regex UploadUrlRegex();

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public ServerAssetServiceImpl(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    public async UnaryResult<CustomAssetInfo[]> GetAssetsAsync()
    {
        return await _db.CustomAssets
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new CustomAssetInfo
            {
                Id = a.Id,
                Kind = a.Kind,
                Name = a.Name,
                Url = a.Url,
                UploaderId = a.UploaderId
            })
            .ToArrayAsync();
    }

    public async UnaryResult<CustomAssetInfo> CreateAssetAsync(CreateCustomAssetRequest request)
    {
        var userId = Context.GetUserId();
        var name = (request.Name ?? string.Empty).Trim().ToLowerInvariant();

        if (!AssetNameRegex().IsMatch(name))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument,
                "Name must be 2-24 characters of a-z, 0-9 or _");
        }

        var url = (request.Url ?? string.Empty).Trim();
        if (!UploadUrlRegex().IsMatch(url))
        {
            // Anything else would let a client point the whole server at a remote image.
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Unsupported image URL");
        }

        // The URL must name a file we actually stored, and it must be small enough.
        var storedName = Path.GetFileName(url);
        var filePath = Path.Combine(_env.ContentRootPath, "Uploads", storedName);
        if (!System.IO.File.Exists(filePath))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Uploaded file not found");
        }

        var maxBytes = request.Kind == CustomAssetKind.Emoji
            ? ChatLimits.MaxEmojiUploadBytes
            : ChatLimits.MaxStickerUploadBytes;

        var sizeBytes = new FileInfo(filePath).Length;
        if (sizeBytes > maxBytes)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument,
                $"File exceeds {maxBytes / 1024} KB limit for this asset type");
        }

        if (await _db.CustomAssets.AnyAsync(a => a.Kind == request.Kind && a.Name == name))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.AlreadyExists, $":{name}: already exists");
        }

        var entity = new CustomAssetEntity
        {
            Id = Guid.NewGuid(),
            Kind = request.Kind,
            Name = name,
            Url = url,
            UploaderId = userId,
            CreatedAt = DateTime.UtcNow
        };

        _db.CustomAssets.Add(entity);
        await _db.SaveChangesAsync();

        return new CustomAssetInfo
        {
            Id = entity.Id,
            Kind = entity.Kind,
            Name = entity.Name,
            Url = entity.Url,
            UploaderId = entity.UploaderId
        };
    }

    public async UnaryResult<bool> DeleteAssetAsync(Guid assetId)
    {
        var userId = Context.GetUserId();

        var asset = await _db.CustomAssets
            .AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.UploaderId })
            .FirstOrDefaultAsync();

        if (asset == null) return false;

        var actorRole = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.GlobalRole)
            .FirstOrDefaultAsync();

        if (asset.UploaderId != userId && actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        await _db.CustomAssets.Where(a => a.Id == assetId).ExecuteDeleteAsync();

        // The file on disk stays: messages and reactions may still reference it, and
        // there is no upload garbage collection to hook into yet.
        return true;
    }
}
