using iPhoneMiror.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;

namespace iPhoneMiror.App.Services;

/// <summary>Creates and tracks the main window and the separate "iPhoneMiror Display" window.</summary>
public sealed class WindowService
{
    private readonly IServiceProvider _services;
    private DisplayWindow? _display;

    public WindowService(IServiceProvider services)
    {
        _services = services;
    }

    public MainWindow? Main { get; set; }

    public DisplayWindow? Display => _display;

    public DisplayWindow ShowDisplayWindow(bool fullscreen)
    {
        if (_display is null)
        {
            _display = _services.GetRequiredService<DisplayWindow>();
            _display.Closed += (_, _) => _display = null;
        }
        _display.Activate();
        _display.SetFullScreen(fullscreen || _display.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
        return _display;
    }

    public nint MainWindowHandle => Main is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(Main);
}
