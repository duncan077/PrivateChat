using ChatServer.Auth;
using MagicOnion;
using MagicOnion.Server;
using Shared.Models;
using Shared.Services;

namespace ChatServer.Services;

[JwtAuthFilter]
public class FileUploadServiceImpl : ServiceBase<IFileUploadService>, IFileUploadService
{
    private readonly IWebHostEnvironment _env;

    public FileUploadServiceImpl(IWebHostEnvironment env)
    {
        _env = env;
    }

    public async UnaryResult<AttachmentInfo> GetUploadUrlAsync(string fileName, string contentType, long sizeBytes)
    {
        var id = Guid.NewGuid();
        var ext = Path.GetExtension(fileName);
        var storedName = $"{id}{ext}";
        var uploadsDir = Path.Combine(_env.ContentRootPath, "Uploads");
        Directory.CreateDirectory(uploadsDir);

        return new AttachmentInfo
        {
            Id = id,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Url = $"/uploads/{storedName}"
        };
    }
}
