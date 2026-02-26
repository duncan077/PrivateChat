using MagicOnion;
using Shared.Models;

namespace Shared.Services;

public interface IFileUploadService : IService<IFileUploadService>
{
    UnaryResult<AttachmentInfo> GetUploadUrlAsync(string fileName, string contentType, long sizeBytes);
}
