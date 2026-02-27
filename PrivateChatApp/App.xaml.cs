namespace PrivateChatApp;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage())
        {
            Title = "PrivateChat",
            Width = 1280,
            Height = 800
        };
    }
}
