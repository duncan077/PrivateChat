namespace ChatAppBlazor.Services;

public interface ISystemNotificationService
{
    Task<bool> ShowChatNotificationAsync(string title, string message, string? navigateUri = null);
}

