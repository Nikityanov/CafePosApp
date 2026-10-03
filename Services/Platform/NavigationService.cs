namespace CafePosApp.Services;

public sealed class NavigationService : INavigationService
{
    public Task GoToOrderDetailsAsync(Guid orderId) =>
        Shell.Current.GoToAsync("order-details", new Dictionary<string, object> { ["OrderId"] = orderId });

    public Task GoBackAsync() => Shell.Current.GoToAsync("..");

    public Task GoToTabAsync(string route) => Shell.Current.GoToAsync($"///{route}");

    /// <summary>
    /// A plain push onto the current stack: the Shell's navigation guard is what keeps the tab bar from
    /// being a way out, and duplicating that here would be a second rule to keep in step.
    /// </summary>
    public Task GoToOpenShiftAsync() => Shell.Current.GoToAsync("open-shift");

    public async Task LeaveOpenShiftAsync()
    {
        // ".." FIRST, before the tab switch. Switching tabs alone leaves the opening screen sitting on
        // the stack of whichever tab was current when it was pushed, and returning to that tab later
        // brings the operator straight back to it — on a shift tab, straight after a close, that meant
        // a page claiming no shift is open over a shift that has been open for a minute. Popping first
        // removes it wherever it actually is (the shift tab after a close, the menu tab after the
        // startup check) and the tab switch then only chooses where they land.
        //
        // Deliberately not "//menu": that sets the current item and says nothing about the stack it
        // left behind, which is the whole problem.
        await Shell.Current.GoToAsync("..");
        await Shell.Current.GoToAsync("///menu");
    }
}
