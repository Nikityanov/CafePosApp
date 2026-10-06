using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using CafePos.Core.Models;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

public sealed class DraftPicker : IDraftPicker
{
    public async Task<Guid?> PickAsync(IReadOnlyList<DraftOrder> drafts, CancellationToken cancellationToken = default)
    {
        if (drafts.Count == 0) return null;

        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return null;

        var popup = new CafePosApp.Views.DraftPickerPopup(drafts);
        var result = await PopupExtensions.ShowPopupAsync<string?>(page, popup, new PopupOptions());
        return result.Result is { Length: > 0 } value && Guid.TryParse(value, out var id) ? id : null;
    }
}

