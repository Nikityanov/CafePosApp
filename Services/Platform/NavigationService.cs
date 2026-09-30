namespace CafePosApp.Services;

public sealed class NavigationService : INavigationService
{
    public Task GoToOrderDetailsAsync(Guid orderId) =>
        Shell.Current.GoToAsync("order-details", new Dictionary<string, object> { ["OrderId"] = orderId });

    public Task GoBackAsync() => Shell.Current.GoToAsync("..");

    public Task GoToTabAsync(string route) => Shell.Current.GoToAsync($"///{route}");
}
