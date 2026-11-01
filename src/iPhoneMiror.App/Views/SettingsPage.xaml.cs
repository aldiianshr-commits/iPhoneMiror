using iPhoneMiror.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace iPhoneMiror.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
