namespace iPhoneMiror.App.Services;

public sealed class NavigationService
{
    public event EventHandler<string>? NavigationRequested;

    public void NavigateTo(string pageTag) => NavigationRequested?.Invoke(this, pageTag);
}
