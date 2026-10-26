using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace ChatApp.Ui.ViewModels;

/// <summary>Small value converters for the DM surface.</summary>
public static class DmConverters
{
    /// <summary>Presence dot: green online, gray offline (and for unknown/null).</summary>
    public static readonly IValueConverter OnlineBrush =
        new FuncValueConverter<bool, IBrush>(online =>
            online ? Brushes.MediumSeaGreen : Brushes.Gray);

    /// <summary>Own messages right-aligned, everyone else's left.</summary>
    public static readonly IValueConverter MessageAlignment =
        new FuncValueConverter<bool, HorizontalAlignment>(mine =>
            mine ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    /// <summary>Tint behind messages that mention the current user.</summary>
    public static readonly IValueConverter MentionBackground =
        new FuncValueConverter<bool, IBrush>(mentioned =>
            mentioned ? new SolidColorBrush(Color.FromArgb(0x2E, 0x22, 0xC5, 0x5E)) : Brushes.Transparent);
}
