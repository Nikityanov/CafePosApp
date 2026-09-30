using CafePos.Core.Models;
using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Views;

namespace CafePosApp.Services;

public sealed class VariantPicker : IVariantPicker
{
    public async Task<string?> PickAsync(string productName, List<ProductVariant> variants)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) throw new InvalidOperationException("No page is available to host the variant picker.");
        var popup = new VariantPickerPopup(productName, variants);
        var result = await CommunityToolkit.Maui.Extensions.PopupExtensions.ShowPopupAsync<string?>(page, popup, new PopupOptions());
        return result.Result;
    }
}
