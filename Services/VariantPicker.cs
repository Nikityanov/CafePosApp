using CafePos.Core.Models;
using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Views;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

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

