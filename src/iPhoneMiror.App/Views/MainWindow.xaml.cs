using iPhoneMiror.App.Services;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace iPhoneMiror.App.Views;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["connect"] = typeof(ConnectPage),
        ["devices"] = typeof(DevicesPage),
        ["mirroring"] = typeof(MirroringPage),
        ["account"] = typeof(AccountPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly ISettingsStore _settings;
    private readonly IMirroringService _mirroring;
    private readonly WindowService _windows;
    private readonly ILogger<MainWindow> _logger;
    private bool _closingConfirmed;

    public MainWindow(DeviceHub hub, NavigationService navigation, ISettingsStore settings, IMirroringService mirroring, WindowService windows, ILogger<MainWindow> logger)
    {
        Hub = hub;
        _settings = settings;
        _mirroring = mirroring;
        _windows = windows;
        _logger = logger;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 900;
            p.PreferredMinimumHeight = 600;
        }
        AppWindow.Closing += OnClosing;

        navigation.NavigationRequested += (_, tag) => Navigate(tag);
        Navigate("dashboard");
    }

    public DeviceHub Hub { get; }

    public void Navigate(string tag)
    {
        if (!Pages.TryGetValue(tag, out var type)) return;
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null && !ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item; // raises SelectionChanged, which navigates.
            return;
        }
        if (ContentFrame.CurrentSourcePageType == type) return;
        NavigationTransitionInfo transition = _settings.Current.Appearance.ReduceAnimations
            ? new SuppressNavigationTransitionInfo()
            : new EntranceNavigationTransitionInfo();
        ContentFrame.Navigate(type, null, transition);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Pages.TryGetValue(tag, out var type) && ContentFrame.CurrentSourcePageType != type)
        {
            NavigationTransitionInfo transition = _settings.Current.Appearance.ReduceAnimations
                ? new SuppressNavigationTransitionInfo()
                : new EntranceNavigationTransitionInfo();
            ContentFrame.Navigate(type, null, transition);
        }
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    public void Minimize()
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingConfirmed) return;
        var mirroring = _mirroring.State is MirroringState.Streaming or MirroringState.Starting or MirroringState.Reconnecting;
        if (mirroring && _settings.Current.General.ConfirmBeforeClosingWhileMirroring)
        {
            args.Cancel = true;
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Stop mirroring and exit?",
                Content = "iPhoneMiror is mirroring an iPhone. Closing the app stops mirroring and closes the Display window.",
                PrimaryButtonText = "Exit",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ((FrameworkElement)Content).ActualTheme,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _closingConfirmed = true;
            await ExitAsync();
            return;
        }
        _closingConfirmed = true;
        args.Cancel = true;
        await ExitAsync();
    }

    private async Task ExitAsync()
    {
        _windows.Display?.Close();
        if (App.Current is { } app) await app.ShutdownAsync();
        Application.Current.Exit();
    }

    /// <summary>Navigates through every page and saves PNG screenshots (CI documentation run). Returns a process exit code.</summary>
    public async Task<int> CaptureScreenshotsAsync(string directory)
    {
        var failures = 0;
        string[] order = ["dashboard", "connect", "devices", "mirroring", "account", "settings"];
        for (var i = 0; i < order.Length; i++)
        {
            try
            {
                Navigate(order[i]);
                await Task.Delay(2500);
                await ScreenshotCapture.SaveAsync(RootGrid, Path.Combine(directory, $"{i + 1:00}-{order[i]}.png"));
                _logger.LogInformation("Captured {Page}.", order[i]);
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogError(ex, "Capturing {Page} failed.", order[i]);
            }
        }
        try
        {
            var display = _windows.ShowDisplayWindow(fullscreen: false);
            await Task.Delay(2500);
            await ScreenshotCapture.SaveAsync((UIElement)display.Content, Path.Combine(directory, "07-display-window.png"));
            display.Close();
        }
        catch (Exception ex)
        {
            failures++;
            _logger.LogError(ex, "Capturing the Display window failed.");
        }
        return failures == 0 ? 0 : 2;
    }
}
