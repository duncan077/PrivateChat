using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace ChatServer.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UploadController : ControllerBase
{
    /// <summary>Copy buffer: big enough to keep syscalls cheap, small enough to stay off the LOH.</summary>
    private const int CopyBufferBytes = 64 * 1024;

    private readonly IWebHostEnvironment _env;

    public UploadController(IWebHostEnvironment env)
    {
        _env = env;
    }

    /// <summary>
    /// Streams one multipart file straight to disk.
    ///
    /// Binding an <c>IFormFile</c> made ASP.NET buffer the entire body first - anything
    /// past ~64 KB spilled to a temp file - and the handler then read that back to copy it
    /// to its destination. For a 50 MB attachment that is two full writes and a full read
    /// where one write will do. Reading the multipart stream directly drops the
    /// intermediate copy, so disk traffic no longer scales at 2x attachment size.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(50 * 1024 * 1024)]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (!IsMultipart(Request.ContentType, out var boundary))
        {
            return BadRequest("Expected multipart/form-data");
        }

        var reader = new MultipartReader(boundary, Request.Body);

        for (var section = await reader.ReadNextSectionAsync(cancellationToken);
             section != null;
             section = await reader.ReadNextSectionAsync(cancellationToken))
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.DispositionType.Equals("form-data")
                || (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue))
            {
                continue;
            }

            var originalName = (disposition.FileNameStar.HasValue
                ? disposition.FileNameStar.Value
                : disposition.FileName.Value) ?? string.Empty;

            // The client-supplied name never reaches the path we write to.
            var safeName = Path.GetFileName(originalName.Trim('"'));
            var id = Guid.NewGuid();
            var storedName = $"{id}{Path.GetExtension(safeName)}";

            var uploadsDir = Path.Combine(_env.ContentRootPath, "Uploads");
            Directory.CreateDirectory(uploadsDir);
            var filePath = Path.Combine(uploadsDir, storedName);

            long written;
            try
            {
                await using var target = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await section.Body.CopyToAsync(target, CopyBufferBytes, cancellationToken);
                written = target.Length;
            }
            catch
            {
                // Do not leave a partial file behind for a request that failed.
                TryDelete(filePath);
                throw;
            }

            if (written == 0)
            {
                TryDelete(filePath);
                return BadRequest("Empty file");
            }

            return Ok(new
            {
                id,
                fileName = safeName,
                contentType = section.ContentType ?? "application/octet-stream",
                sizeBytes = written,
                url = $"/uploads/{storedName}"
            });
        }

        return BadRequest("No file section found");
    }

    private static bool IsMultipart(string? contentType, out string boundary)
    {
        boundary = string.Empty;

        if (string.IsNullOrEmpty(contentType)
            || !contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase)
            || !MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            return false;
        }

        var parsed = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        boundary = parsed;
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}

/// <summary>
/// Removes the form value providers for an action so MVC does not read - and buffer -
/// the multipart body before the handler can stream it.
/// </summary>
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
