using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Markup;

namespace iPhoneMiror.App.Services;

/// <summary>Startup diagnostics: isolates which XAML construct fails to load (used by the CI smoke run).</summary>
internal static class XamlDiagnostics
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static readonly (string Name, string Xaml)[] Probes =
    [
        ("Missing key (control)", $"<Border {Ns} Style=\"{{StaticResource DoesNotExist}}\" />"),
        ("Card padding", $"<Border {Ns} Padding=\"{{StaticResource CardPadding}}\" />"),
        ("Card brush", $"<Border {Ns} Background=\"{{ThemeResource CardBackgroundBrush}}\" />"),
        ("Card style", $"<Border {Ns} Style=\"{{StaticResource CardStyle}}\" />"),
        ("Pill style", $"<Border {Ns} Style=\"{{StaticResource PillStyle}}\" />"),
        ("Label style", $"<TextBlock {Ns} Style=\"{{StaticResource LabelStyle}}\" />"),
        ("Page title style", $"<TextBlock {Ns} Style=\"{{StaticResource PageTitleStyle}}\" />"),
        ("Subtle brush", $"<TextBlock {Ns} Foreground=\"{{ThemeResource SubtleTextBrush}}\" />"),
        ("Accent button", $"<Button {Ns} Style=\"{{StaticResource AccentButtonStyle}}\" />"),
        ("App icon image", $"<Image {Ns} Source=\"ms-appx:///Assets/AppIcon.png\" />"),
        ("NavigationView", $"<NavigationView {Ns} PaneDisplayMode=\"Left\" OpenPaneLength=\"232\" IsBackButtonVisible=\"Collapsed\" IsSettingsVisible=\"False\"><NavigationView.MenuItems><NavigationViewItem Content=\"A\" Tag=\"a\"><NavigationViewItem.Icon><FontIcon Glyph=\"&#xE80F;\" /></NavigationViewItem.Icon></NavigationViewItem></NavigationView.MenuItems><Frame /></NavigationView>"),
        ("SelectorBar", $"<SelectorBar {Ns}><SelectorBarItem Text=\"USB\" /></SelectorBar>"),
        ("InfoBar", $"<InfoBar {Ns} IsOpen=\"True\" Title=\"t\" Message=\"m\" />"),
        ("ItemsRepeater", $"<ItemsRepeater {Ns}><ItemsRepeater.Layout><UniformGridLayout MinItemWidth=\"340\" /></ItemsRepeater.Layout></ItemsRepeater>"),
        ("RadioButtons", $"<RadioButtons {Ns} Header=\"h\"><RadioButton Content=\"a\" /></RadioButtons>"),
        ("NumberBox", $"<NumberBox {Ns} SpinButtonPlacementMode=\"Compact\" />"),
    ];

    public static void Run(ILogger logger)
    {
        var res = Microsoft.UI.Xaml.Application.Current.Resources;
        logger.LogInformation("App theme {Theme}; HighContrast {Hc}; top-level keys {Count}; merged {Merged}; theme dictionaries [{Themes}]",
            Microsoft.UI.Xaml.Application.Current.RequestedTheme, new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast,
            res.Count, res.MergedDictionaries.Count, string.Join(",", res.ThemeDictionaries.Keys));
        foreach (var key in new[] { "CardStyle", "PageTitleStyle", "CardCornerRadius", "SystemAccentColor", "SubtleTextBrush", "AccentButtonStyle" })
        {
            try
            {
                var found = res.TryGetValue(key, out var v);
                logger.LogInformation("Key {Key}: contains={Contains} found={Found} type={Type}", key, res.ContainsKey(key), found, v?.GetType().Name);
            }
            catch (Exception ex)
            {
                logger.LogError("Key {Key}: lookup threw {Type} 0x{HResult:X8} {Message}", key, ex.GetType().Name, ex.HResult, ex.Message);
            }
        }
        foreach (var (name, xaml) in Probes)
        {
            try
            {
                XamlReader.Load(xaml);
                logger.LogInformation("XAML probe OK: {Name}", name);
            }
            catch (Exception ex)
            {
                logger.LogError("XAML probe FAILED: {Name}: 0x{HResult:X8} {Message}", name, ex.HResult, ex.Message);
            }
        }
        foreach (var page in new Func<object>[]
        {
            () => new Views.DashboardPage(), () => new Views.ConnectPage(), () => new Views.DevicesPage(),
            () => new Views.MirroringPage(), () => new Views.AccountPage(), () => new Views.SettingsPage(),
        })
        {
            try
            {
                var p = page();
                logger.LogInformation("Page OK: {Page}", p.GetType().Name);
            }
            catch (Exception ex)
            {
                logger.LogError("Page FAILED: {Message} {Inner}", ex.Message, ex.InnerException?.Message);
            }
        }
    }
}
