using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ChatApp.Ui.ViewModels;
using ChatApp.Ui.Views;

namespace ChatApp.Ui;

/// <summary>
/// Maps a view model to its view.
/// <para>
/// An explicit switch rather than the template's <c>Type.GetType</c> +
/// <c>Activator.CreateInstance</c> convention: reflection over type names is exactly
/// what NativeAOT trimming removes, and the compile error when a case is missing here
/// is friendlier than "Not Found: …View" at runtime. Add one line per new view.
/// </para>
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        MainViewModel => new MainView(),
        ShellViewModel => new ShellView(),
        LoginViewModel => new LoginView(),
        RegisterViewModel => new RegisterView(),
        CallViewModel => new CallView(),
        SettingsViewModel => new SettingsView(),
        ScreenSharePickerViewModel => new ScreenSharePickerView(),
        null => null,
        _ => new TextBlock { Text = $"No view mapped for {param.GetType().Name}" }
    };

    public bool Match(object? data) => data is ViewModelBase;
}
