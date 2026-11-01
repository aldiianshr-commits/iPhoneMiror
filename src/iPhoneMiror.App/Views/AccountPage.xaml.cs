using iPhoneMiror.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace iPhoneMiror.App.Views;

public sealed partial class AccountPage : Page
{
    public AccountPage()
    {
        ViewModel = App.GetService<AccountViewModel>();
        InitializeComponent();
    }

    public AccountViewModel ViewModel { get; }
}
