using iPhoneMiror.App.Services;
using iPhoneMiror.App.ViewModels;
using iPhoneMiror.App.Views;
using iPhoneMiror.Connection.Pairing;
using iPhoneMiror.Connection.Services;
using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Account;
using iPhoneMiror.Core.Compatibility;
using iPhoneMiror.Core.Data;
using iPhoneMiror.Core.Logging;
using iPhoneMiror.Core.Settings;
using iPhoneMiror.Core.Util;
using iPhoneMiror.DeviceDiscovery;
using iPhoneMiror.DeviceDiscovery.Bluetooth;
using iPhoneMiror.Input;
using iPhoneMiror.Mirroring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace iPhoneMiror.App;

public partial class App : Application
{
    private readonly string[] _args;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    public App(string[] args)
    {
        _args = args;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            _logger?.LogCritical(e.Exception, "Unhandled UI exception.");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };
    }

    public static new App? Current => Application.Current as App;

    public static T GetService<T>() where T : notnull =>
        (Current?._services ?? throw new InvalidOperationException("Services are not initialized.")).GetRequiredService<T>();

    public bool IsScreenshotMode => _args.Contains("--capture-screenshots");

    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        if (IsScreenshotMode)
        {
            // Watchdog: a CI documentation run must never hang.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(170));
                _logger?.LogCritical("Screenshot run watchdog expired.");
                await Task.Delay(500);
                Environment.Exit(3);
            });
        }
        try
        {
            await LaunchAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Startup failed.");
            if (IsScreenshotMode) Environment.Exit(4);
            throw;
        }
    }

    private async Task LaunchAsync()
    {
        var paths = new AppPaths(Environment.GetEnvironmentVariable("IPHONEMIROR_DATA_DIR"));
        paths.EnsureCreated();

        var settingsStore = new JsonSettingsStore(paths.SettingsFile, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsStore>.Instance);
        await settingsStore.LoadAsync();
        var settings = settingsStore.Current;

        var fileLogs = new FileLoggerProvider(paths.LogDirectory, settings.Logging.RetainDays) { MinimumLevel = (LogLevel)settings.Logging.MinimumLevel };
        _services = ConfigureServices(paths, settingsStore, fileLogs);
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("iPhoneMiror {Version} starting ({Runtime}, {Arch}).",
            typeof(App).Assembly.GetName().Version, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

        _services.GetRequiredService<UiDispatcher>().Initialize(DispatcherQueue.GetForCurrentThread());

        try { await _services.GetRequiredService<IDeviceRepository>().InitializeAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Database initialization failed."); }

        if (!IsScreenshotMode)
        {
            try { SystemIntegration.RegisterProtocol(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Registering the iphonemiror:// protocol failed."); }
        }

        var themes = _services.GetRequiredService<ThemeService>();
        themes.ForceSolidBackground = IsScreenshotMode;
        var main = _services.GetRequiredService<MainWindow>();
        _services.GetRequiredService<WindowService>().Main = main;
        themes.Register(main);

        _logger.LogInformation("Main window created.");
        var minimized = _args.Contains("--minimized") || (settings.Startup.StartMinimized && !IsScreenshotMode);
        main.Activate();
        if (minimized) main.Minimize();

        await _services.GetRequiredService<IAppleAccountService>().LoadAsync();
        await _services.GetRequiredService<IDeviceConnectionService>().StartAsync();
        _logger.LogInformation("Services started.");

        if (IsScreenshotMode)
        {
            var dir = _args.SkipWhile(a => a != "--capture-screenshots").Skip(1).FirstOrDefault() ?? Path.Combine(paths.Root, "screenshots");
            var code = await main.CaptureScreenshotsAsync(dir);
            _logger.LogInformation("Screenshot run finished with code {Code}.", code);
            await ShutdownAsync();
            Environment.Exit(code);
            return;
        }

        await HandleActivationAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
        if (settings.Startup.AutoStartMirroringForLastDevice && settings.Startup.LastDeviceUdid is { } last)
        {
            _ = AutoStartMirroringAsync(last);
        }
    }

    private static ServiceProvider ConfigureServices(AppPaths paths, JsonSettingsStore settingsStore, FileLoggerProvider fileLogs)
    {
        var s = new ServiceCollection();
        var settings = settingsStore.Current;

        s.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(fileLogs);
        });
        s.AddSingleton(fileLogs);
        s.AddSingleton(paths);
        s.AddSingleton<ISettingsStore>(settingsStore);
        s.AddSingleton<IDeviceRepository>(_ => SqliteDeviceRepository.ForFile(paths.DatabaseFile));
        s.AddSingleton(_ => new CompatibilityEvaluator());

        // Connection
        s.AddSingleton<IUsbmuxClient>(_ => new UsbmuxClient(settings.Connection.UsbmuxHost, settings.Connection.UsbmuxPort));
        s.AddSingleton<IDeviceTransport, DeviceTransport>();
        s.AddSingleton<IDataProtector, DpapiDataProtector>();
        s.AddSingleton<IPairRecordStore>(sp => new PairRecordStore(sp.GetRequiredService<IUsbmuxClient>(), paths.PairRecordDirectory,
            sp.GetRequiredService<IDataProtector>(), sp.GetRequiredService<ILogger<PairRecordStore>>()));
        s.AddSingleton(_ => new DeviceConnectionOptions
        {
            AutoPair = settings.Connection.AutoPairOnConnect,
            AutoReconnect = settings.Connection.AutoReconnect,
            EnableWifiDevices = settings.Connection.EnableWifiDevices,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(Math.Max(100, settings.Connection.ReconnectInitialDelayMs)),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(Math.Max(settings.Connection.ReconnectInitialDelayMs, settings.Connection.ReconnectMaxDelayMs)),
        });
        s.AddSingleton<IDeviceConnectionService>(sp => new DeviceConnectionService(
            sp.GetRequiredService<IUsbmuxClient>(), sp.GetRequiredService<IDeviceTransport>(), sp.GetRequiredService<IPairRecordStore>(),
            sp.GetRequiredService<DeviceConnectionOptions>(), sp.GetRequiredService<ILogger<DeviceConnectionService>>(),
            sp.GetRequiredService<IDeviceRepository>()));

        // Discovery
        s.AddSingleton<IBluetoothScanner, WindowsBluetoothScanner>();
        s.AddSingleton<IDeviceDiscoveryService>(sp =>
        {
            var connections = sp.GetRequiredService<IDeviceConnectionService>();
            return new DeviceDiscoveryService(sp.GetRequiredService<IBluetoothScanner>(), sp.GetRequiredService<ILogger<DeviceDiscoveryService>>(),
                mac => connections.Devices.FirstOrDefault(d => string.Equals(d.WifiAddress, mac, StringComparison.OrdinalIgnoreCase))?.DisplayName);
        });

        // Input + mirroring
        s.AddSingleton<WdaInputForwarder>();
        s.AddSingleton<IInputForwarder>(sp => sp.GetRequiredService<WdaInputForwarder>());
        s.AddSingleton<IMirrorQualityConfigurator>(sp => sp.GetRequiredService<WdaInputForwarder>());
        s.AddSingleton<IMirroringService>(sp => new MirroringService(
            sp.GetRequiredService<IDeviceConnectionService>(), sp.GetRequiredService<IDeviceTransport>(), sp.GetRequiredService<ILogger<MirroringService>>(),
            sp.GetRequiredService<IMirrorQualityConfigurator>(),
            () => settingsStore.Current.Mirroring.UxPlayPath,
            () => settingsStore.Current.Mirroring.AirPlayReceiverName));

        // Apple Account
        s.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        s.AddSingleton<IBrowserLauncher, ShellBrowserLauncher>();
        s.AddSingleton(_ => new AppleIdTokenValidator(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
        s.AddSingleton<IAppleAccountService, AppleSignInService>();

        // UI services and view models
        s.AddSingleton<UiDispatcher>();
        s.AddSingleton<ThemeService>();
        s.AddSingleton<NavigationService>();
        s.AddSingleton<WindowService>();
        s.AddSingleton<DeviceHub>();
        s.AddSingleton<MirroringViewModel>();
        s.AddSingleton<DashboardViewModel>();
        s.AddSingleton<ConnectViewModel>();
        s.AddSingleton<DevicesViewModel>();
        s.AddSingleton<AccountViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainWindow>();
        s.AddTransient<DisplayWindow>();

        return s.BuildServiceProvider();
    }

    private async Task AutoStartMirroringAsync(string udid)
    {
        var connections = GetService<IDeviceConnectionService>();
        var until = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < until)
        {
            if (connections.Find(udid) is { State: Core.Models.DeviceConnectionState.Connected })
            {
                var vm = GetService<MirroringViewModel>();
                vm.SelectedDevice = GetService<DeviceHub>().Find(udid);
                if (vm.StartCommand.CanExecute(null)) await vm.StartCommand.ExecuteAsync(null);
                vm.OpenWindowCommand.Execute(null);
                return;
            }
            await Task.Delay(1000);
        }
        _logger?.LogInformation("Auto-start mirroring skipped: {Udid} did not connect within a minute.", udid);
    }

    /// <summary>Called when a second instance (e.g. the iphonemiror:// callback) redirects its activation here.</summary>
    public void OnRedirectedActivation(AppActivationArguments args)
    {
        var dispatcher = _services?.GetService<UiDispatcher>();
        dispatcher?.Post(async () =>
        {
            GetService<WindowService>().Main?.BringToFront();
            await HandleActivationAsync(args);
        });
    }

    private async Task HandleActivationAsync(AppActivationArguments args)
    {
        Uri? uri = null;
        if (args.Kind == ExtendedActivationKind.Protocol && args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs p)
        {
            uri = p.Uri;
        }
        else if (args.Kind == ExtendedActivationKind.Launch && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs l)
        {
            uri = FindCallback(l.Arguments);
        }
        uri ??= FindCallback(string.Join(' ', _args));
        if (uri is null) return;

        _logger?.LogInformation("Received Sign in with Apple callback.");
        GetService<NavigationService>().NavigateTo("account");
        await GetService<IAppleAccountService>().CompleteSignInAsync(uri);
    }

    private static Uri? FindCallback(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = text.IndexOf(AppleSignInService.CallbackScheme + "://", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var end = text.IndexOfAny([' ', '"'], start);
        var candidate = end < 0 ? text[start..] : text[start..end];
        return Uri.TryCreate(candidate, UriKind.Absolute, out var u) ? u : null;
    }

    public async Task ShutdownAsync()
    {
        if (_services is null) return;
        try
        {
            await _services.GetRequiredService<IMirroringService>().StopAsync();
            await _services.GetRequiredService<IDeviceConnectionService>().StopAsync();
            await _services.GetRequiredService<IDeviceDiscoveryService>().DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during shutdown.");
        }
        _services.GetRequiredService<FileLoggerProvider>().Dispose();
    }
}
