using iPhoneMiror.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iPhoneMiror.App.Views;

public sealed partial class ConnectPage : Page
{
    public ConnectPage()
    {
        ViewModel = App.GetService<ConnectViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            MethodBar.SelectedItem = MethodBar.Items[Math.Clamp(ViewModel.SelectedMethod, 0, 2)];
            await ViewModel.CheckServiceCommand.ExecuteAsync(null);
        };
    }

    public ConnectViewModel ViewModel { get; }

    private void MethodBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.SelectedMethod = sender.Items.IndexOf(sender.SelectedItem);
        if (ViewModel.SelectedMethod == 2) ViewModel.RefreshBluetoothStatusCommand.Execute(null);
    }

    private void PairButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceItemViewModel d }) ViewModel.PairCommand.Execute(d);
    }

    private void BluetoothPair_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiscoveredItem d }) ViewModel.PairBluetoothCommand.Execute(d);
    }
}
