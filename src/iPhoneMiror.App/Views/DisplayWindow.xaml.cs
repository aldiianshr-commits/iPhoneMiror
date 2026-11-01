using System.Runtime.InteropServices.WindowsRuntime;
using iPhoneMiror.App.Services;
using iPhoneMiror.App.ViewModels;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Settings;
using iPhoneMiror.Core.Util;
using iPhoneMiror.Input.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace iPhoneMiror.App.Views;

/// <summary>
/// "iPhoneMiror Display": an independent, resizable window showing the live iPhone screen. Pointer and keyboard input on the
/// image is translated into touches and forwarded to the iPhone through the active <see cref="IInputForwarder"/>.
/// </summary>
public sealed partial class DisplayWindow : Window
{
    private readonly IMirroringService _mirroring;
    private readonly IInputForwarder _input;
    private readonly ISettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<DisplayWindow> _logger;
    private readonly GestureTranslator _gestures = new();
    private readonly DispatcherQueueTimer _flushTimer;
    private readonly DispatcherQueueTimer _typingTimer;
    private readonly System.Text.StringBuilder _typing = new();
    private MirrorFrame? _pendingFrame;
    private MirrorFrame? _shownFrame;
    private int _decoding;
    private (int W, int H) _frameSize;
    private bool? _lastLandscape;
    private uint? _capturedPointer;

    public DisplayWindow(MirroringViewModel viewModel, IMirroringService mirroring, IInputForwarder input, ISettingsStore settings, AppPaths paths,
        ThemeService themes, ILogger<DisplayWindow> logger)
    {
        ViewModel = viewModel;
        _mirroring = mirroring;
        _input = input;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 920));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 280;
            p.PreferredMinimumHeight = 360;
        }
        themes.Register(this);

        var dq = DispatcherQueue;
        _flushTimer = dq.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(50);
        _flushTimer.Tick += (_, _) => _ = DispatchGesturesAsync(_gestures.Flush(DateTimeOffset.UtcNow));
        _flushTimer.Start();
        _typingTimer = dq.CreateTimer();
        _typingTimer.Interval = TimeSpan.FromMilliseconds(150);
        _typingTimer.IsRepeating = false;
        _typingTimer.Tick += (_, _) => _ = FlushTypingAsync();

        _mirroring.FrameReceived += OnFrameReceived;
        ViewModel.PropertyChanged += OnViewModelChanged;
        if (_mirroring.LatestFrame is { } latest) OnFrameReceived(this, latest);
        UpdatePlaceholder();

        Closed += (_, _) =>
        {
            _mirroring.FrameReceived -= OnFrameReceived;
            ViewModel.PropertyChanged -= OnViewModelChanged;
            _flushTimer.Stop();
            _typingTimer.Stop();
        };
    }

    public MirroringViewModel ViewModel { get; }

    // ------------------------------------------------------------------ frames

    private void OnFrameReceived(object? sender, MirrorFrame frame)
    {
        Interlocked.Exchange(ref _pendingFrame, frame);
        if (Interlocked.CompareExchange(ref _decoding, 1, 0) == 0)
        {
            DispatcherQueue.TryEnqueue(async () => await DecodeLoopAsync());
        }
    }

    /// <summary>Decodes only the newest frame; frames that arrive while decoding are skipped (keeps latency low).</summary>
    private async Task DecodeLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _pendingFrame, null) is { } frame)
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(frame.Jpeg.AsBuffer());
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                Screen.Source = bitmap;
                _shownFrame = frame;
                var size = frame.TryGetPixelSize() ?? (bitmap.PixelWidth, bitmap.PixelHeight);
                if (size != _frameSize)
                {
                    _frameSize = size;
                    OnOrientationMaybeChanged();
                }
                UpdatePlaceholder();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Frame decode failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _decoding, 0);
            if (Volatile.Read(ref _pendingFrame) is not null && Interlocked.CompareExchange(ref _decoding, 1, 0) == 0)
            {
                DispatcherQueue.TryEnqueue(async () => await DecodeLoopAsync());
            }
        }
    }

    private void OnOrientationMaybeChanged()
    {
        if (_frameSize.W <= 0 || _frameSize.H <= 0) return;
        var landscape = _frameSize.W > _frameSize.H;
        if (_lastLandscape == landscape) return;
        _lastLandscape = landscape;
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) return;
        FitWindowToAspect(landscape);
    }

    private void FitWindowToAspect(bool landscape)
    {
        var aspect = _frameSize.W > 0 && _frameSize.H > 0 ? (double)Math.Max(_frameSize.W, _frameSize.H) / Math.Min(_frameSize.W, _frameSize.H) : 2.16;
        var current = AppWindow.Size;
        const int chrome = 44 + 52; // title + toolbar (approx. physical px at 100%)
        if (landscape)
        {
            var height = Math.Max(360, Math.Min(current.Height, 700));
            var width = (int)((height - chrome) * aspect);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(480, width), height));
        }
        else
        {
            var height = Math.Max(600, Math.Max(current.Height, 860));
            var width = (int)((height - chrome) / aspect);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(300, width), height));
        }
        ZoomFit_Click(this, new RoutedEventArgs());
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MirroringViewModel.IsStreaming) or nameof(MirroringViewModel.IsRunning) or nameof(MirroringViewModel.IsAirPlay))
        {
            if (!ViewModel.IsRunning)
            {
                Screen.Source = null;
                _shownFrame = null;
            }
            UpdatePlaceholder();
        }
    }

    private void UpdatePlaceholder()
    {
        var hasImage = Screen.Source is not null && ViewModel.IsRunning;
        Placeholder.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Stage.Width = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : e.NewSize.Width;
        Stage.Height = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : e.NewSize.Height;
    }

    // ------------------------------------------------------------------ toolbar

    public void SetFullScreen(bool fullscreen)
    {
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) =>
        SetFullScreen(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Scroller.ChangeView(null, null, Math.Min(4f, Scroller.ZoomFactor * 1.25f));

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Scroller.ChangeView(null, null, Math.Max(0.5f, Scroller.ZoomFactor / 1.25f));

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Scroller.ChangeView(0, 0, 1f);

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        var size = AppWindow.Size;
        var landscape = size.Width <= size.Height;
        _lastLandscape = landscape;
        FitWindowToAspect(landscape);
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var frame = _shownFrame;
        if (frame is null) return;
        try
        {
            var folder = string.IsNullOrWhiteSpace(_settings.Current.General.ScreenshotFolder) ? _paths.DefaultScreenshotDirectory : _settings.Current.General.ScreenshotFolder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"iPhoneMiror_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, frame.Jpeg);
            ShowToast(InfoBarSeverity.Success, "Screenshot saved", path);
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "Screenshot failed", ex.Message);
        }
    }

    private void InputToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton t) ViewModel.IsInputEnabled = t.IsChecked == true;
    }

    private async void Home_Click(object sender, RoutedEventArgs e) => await RunInputAsync(() => _input.PressButtonAsync(HardwareButton.Home));

    private void ShowToast(InfoBarSeverity severity, string title, string message)
    {
        Toast.Severity = severity;
        Toast.Title = title;
        Toast.Message = message;
        Toast.IsOpen = true;
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(4);
        t.IsRepeating = false;
        t.Tick += (_, _) => Toast.IsOpen = false;
        t.Start();
    }

    // ------------------------------------------------------------------ input

    private bool InputActive => ViewModel.IsInputEnabled && ViewModel.InputAvailable && _shownFrame is not null;

    private DevicePoint? Map(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Screen).Position;
        var device = _input.ScreenSizePoints ?? (_frameSize.W / 3.0, _frameSize.H / 3.0);
        return iPhoneMiror.Input.Mapping.CoordinateMapper.ViewToDevice(p.X, p.Y, Screen.ActualWidth, Screen.ActualHeight,
            _frameSize.W, _frameSize.H, device.Item1, device.Item2);
    }

    private void UpdateDeviceSize()
    {
        if (_input.ScreenSizePoints is { } s)
        {
            var landscape = _frameSize.W > _frameSize.H;
            _gestures.DeviceSize = landscape == (s.Width > s.Height) ? s : (s.Height, s.Width);
        }
    }

    private void Screen_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Scroller.Focus(FocusState.Pointer);
        if (!InputActive) return;
        var props = e.GetCurrentPoint(Screen).Properties;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !props.IsLeftButtonPressed)
        {
            // Right click = Home (like a "back to home" shortcut).
            if (props.IsRightButtonPressed) _ = RunInputAsync(() => _input.PressButtonAsync(HardwareButton.Home));
            return;
        }
        if (Map(e) is not { } point) return;
        UpdateDeviceSize();
        Screen.CapturePointer(e.Pointer);
        _capturedPointer = e.Pointer.PointerId;
        _gestures.OnPressed(point, DateTimeOffset.UtcNow);
        e.Handled = true;
    }

    private void Screen_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointer != e.Pointer.PointerId || !_gestures.IsPressed) return;
        if (Map(e) is { } point) _gestures.OnMoved(point);
    }

    private void Screen_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointer != e.Pointer.PointerId) return;
        Screen.ReleasePointerCapture(e.Pointer);
        _capturedPointer = null;
        var gestures = _gestures.OnReleased(Map(e), DateTimeOffset.UtcNow);
        _ = DispatchGesturesAsync(gestures);
        e.Handled = true;
    }

    private void Screen_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _capturedPointer = null;
        _ = DispatchGesturesAsync(_gestures.OnReleased(null, DateTimeOffset.UtcNow));
    }

    private void Screen_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!InputActive) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl) return; // Ctrl+wheel zooms the view (handled by ScrollViewer).
        if (Map(e) is not { } point) return;
        UpdateDeviceSize();
        var delta = e.GetCurrentPoint(Screen).Properties.MouseWheelDelta;
        _ = DispatchGesturesAsync(_gestures.OnWheel(delta, point, DateTimeOffset.UtcNow));
        e.Handled = true;
    }

    private async Task DispatchGesturesAsync(IReadOnlyList<Gesture> gestures)
    {
        foreach (var g in gestures)
        {
            await RunInputAsync(() => g switch
            {
                TapGesture t => _input.TapAsync(t.Point),
                DoubleTapGesture d => _input.DoubleTapAsync(d.Point),
                LongPressGesture l => _input.LongPressAsync(l.Point, l.Duration),
                SwipeGesture s => _input.DragAsync(s.From, s.To, s.Duration),
                _ => Task.CompletedTask,
            });
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F11)
        {
            Fullscreen_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Escape && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
            return;
        }
        if (!InputActive) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var t = KeyboardTranslator.TranslateKey((int)e.Key, ctrl, shift, alt);
        switch (t.Action)
        {
            case KeyAction.Home:
                _ = RunInputAsync(() => _input.PressButtonAsync(HardwareButton.Home));
                e.Handled = true;
                break;
            case KeyAction.TypeText:
                QueueTyping(t.Text!);
                e.Handled = true;
                break;
        }
    }

    private void Root_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (!InputActive) return;
        var text = KeyboardTranslator.TranslateCharacter(args.Character);
        if (text is null) return;
        QueueTyping(text);
        args.Handled = true;
    }

    private void QueueTyping(string text)
    {
        _typing.Append(text);
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    private async Task FlushTypingAsync()
    {
        if (_typing.Length == 0) return;
        var text = _typing.ToString();
        _typing.Clear();
        await RunInputAsync(() => _input.TypeTextAsync(text));
    }

    private async Task RunInputAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Input forwarding failed.");
            ShowToast(InfoBarSeverity.Warning, "Input not delivered", ex.Message);
        }
    }
}
