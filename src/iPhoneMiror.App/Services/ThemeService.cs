using iPhoneMiror.Core.Settings;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace iPhoneMiror.App.Services;

/// <summary>Applies theme (dark/light/system) and backdrop (Mica or solid) to all app windows.</summary>
public sealed class ThemeService
{
    private readonly ISettingsStore _settings;
    private readonly List<Window> _windows = [];

    public ThemeService(ISettingsStore settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) => ApplyAll();
    }

    /// <summary>Set by screenshot mode to force solid backgrounds (Mica is not captured by RenderTargetBitmap).</summary>
    public bool ForceSolidBackground { get; set; }

    public void Register(Window window)
    {
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        Apply(window);
    }

    public void ApplyAll()
    {
        foreach (var w in _windows.ToList()) Apply(w);
    }

    public ElementTheme CurrentTheme => _settings.Current.Appearance.Theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.System => ElementTheme.Default,
        _ => ElementTheme.Dark,
    };

    public void Apply(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = CurrentTheme;
            var useMica = _settings.Current.Appearance.UseMica && !ForceSolidBackground && MicaController.IsSupported();
            if (root is Microsoft.UI.Xaml.Controls.Panel panel)
            {
                var isLight = CurrentTheme == ElementTheme.Light ||
                    (CurrentTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);
                var dict = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[isLight ? "Light" : "Dark"];
                panel.Background = useMica ? null : (Brush)dict["AppBackgroundBrush"];
            }
            window.SystemBackdrop = useMica ? new MicaBackdrop { Kind = MicaKind.BaseAlt } : null;
        }
        if (window.AppWindow.TitleBar is { } tb && Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var dark = CurrentTheme != ElementTheme.Light;
            tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x18, 0, 0, 0);
            tb.ButtonHoverForegroundColor = tb.ButtonForegroundColor;
        }
    }
}
