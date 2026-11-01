using iPhoneMiror.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace iPhoneMiror.App.Converters;

/// <summary>Static helpers for x:Bind function bindings.</summary>
public static class Bind
{
    public static bool Not(bool value) => !value;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNone(int count) => count > 0 ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Brush Indicator(IndicatorKind kind) => (Brush)Application.Current.Resources[kind switch
    {
        IndicatorKind.Good => "StatusGoodBrush",
        IndicatorKind.Warn => "StatusWarnBrush",
        IndicatorKind.Bad => "StatusBadBrush",
        _ => "StatusIdleBrush",
    }];

    public static Brush Status(bool good) => Indicator(good ? IndicatorKind.Good : IndicatorKind.Bad);

    public static Brush DeviceStatus(bool connected, bool attached) => Indicator(connected ? IndicatorKind.Good : attached ? IndicatorKind.Warn : IndicatorKind.Idle);

    public static string ServiceText(bool available) => available ? "Service ready" : "Service offline";

    public static string ToggleText(bool running, string whenRunning, string whenStopped) => running ? whenRunning : whenStopped;

    public static string Percent(double value) => $"{value:0}%";

    public static string Fps(double value) => $"{value:0} fps";

    public static double ToDouble(int value) => value;

    public static string SupportText(iPhoneMiror.Core.Models.FeatureSupport s) => s switch
    {
        iPhoneMiror.Core.Models.FeatureSupport.Verified => "Verified on device",
        iPhoneMiror.Core.Models.FeatureSupport.ImplementedUnverified => "Implemented · not yet verified",
        iPhoneMiror.Core.Models.FeatureSupport.RequiresComponent => "Needs extra component",
        _ => "Not available",
    };

    public static string Count(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);
}
