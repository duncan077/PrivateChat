namespace ChatAppBlazor.State;

public class MembersPanelStore
{
    public bool IsVisible { get; private set; } = false;

    public event Action? OnChange;

    public void Toggle()
    {
        IsVisible = !IsVisible;
        OnChange?.Invoke();
    }

    public void SetVisible(bool visible)
    {
        if (IsVisible == visible)
        {
            return;
        }

        IsVisible = visible;
        OnChange?.Invoke();
    }
}
