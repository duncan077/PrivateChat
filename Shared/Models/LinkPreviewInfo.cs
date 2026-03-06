namespace Shared.Models;

public class LinkPreviewInfo
{
    public string Url { get; set; } = string.Empty;
    public string? CanonicalUrl { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? SiteName { get; set; }
    public string? ImageUrl { get; set; }
    public string? IconUrl { get; set; }
}
