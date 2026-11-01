using iPhoneMiror.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace iPhoneMiror.App.Views;

public sealed partial class MirroringPage : Page
{
    public MirroringPage()
    {
        ViewModel = App.GetService<MirroringViewModel>();
        InitializeComponent();
    }

    public MirroringViewModel ViewModel { get; }
}
