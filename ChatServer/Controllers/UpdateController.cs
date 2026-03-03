using Microsoft.AspNetCore.Mvc;

namespace ChatServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UpdateController : ControllerBase
{
    private readonly IConfiguration _config;

    public UpdateController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet]
    public IActionResult GetLatestVersion([FromQuery] string? platform = null)
    {
        var section = _config.GetSection("AppUpdate");
        var version = section["LatestVersion"] ?? "1.0.0";
        var normalizedPlatform = NormalizePlatform(platform);
        var downloadUrl = ResolveDownloadUrl(section, normalizedPlatform);
        var releaseNotes = section["ReleaseNotes"] ?? "";

        return Ok(new
        {
            version,
            downloadUrl,
            releaseNotes,
            platform = normalizedPlatform
        });
    }

    private static string NormalizePlatform(string? platform)
    {
        var value = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "android" => "android",
            "linux" => "linux",
            "ios" => "ios",
            "maccatalyst" => "maccatalyst",
            "mac" => "maccatalyst",
            "macos" => "maccatalyst",
            "windows" => "windows",
            "win" => "windows",
            _ => "windows"
        };
    }

    private static string ResolveDownloadUrl(IConfigurationSection section, string platform)
    {
        var platformUrl = section[$"DownloadUrls:{platform}"];
        if (!string.IsNullOrWhiteSpace(platformUrl))
        {
            return platformUrl;
        }

        var legacyPlatformUrl = section[$"{platform}DownloadUrl"];
        if (!string.IsNullOrWhiteSpace(legacyPlatformUrl))
        {
            return legacyPlatformUrl;
        }

        return section["DownloadUrl"] ?? string.Empty;
    }
}
