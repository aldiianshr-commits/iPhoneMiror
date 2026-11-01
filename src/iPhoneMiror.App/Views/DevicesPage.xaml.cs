using iPhoneMiror.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace iPhoneMiror.App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        ViewModel = App.GetService<DevicesViewModel>();
        InitializeComponent();
    }

    public DevicesViewModel ViewModel { get; }
}
