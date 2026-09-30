using CommunityToolkit.Mvvm.Input;

namespace CafePosApp.ViewModels;

/// <summary>
/// What the shared catalogue form footer needs from a form ViewModel: a save action, a cancel
/// action, and the validation message shown above them.
/// </summary>
/// <remarks>
/// The four form ViewModels each grew these members independently, and the footer used to be a
/// <c>DataTemplate</c> instantiated four times — which is not possible in .NET MAUI 10, because no
/// cross-platform control has a <c>ContentTemplate</c> (see Views/CatalogFormsPage.xaml). The
/// footer is built in code now, and a Binding resolves by name at run time: a ViewModel missing
/// one of these members would silently render an empty footer rather than fail loudly. Naming the
/// contract turns that silent no-op into a compile error. The members themselves stay where they
/// are, so the existing <c>{Binding SaveText}</c>-style paths in the VMs are unchanged.
/// </remarks>
public interface IFormFooterSource
{
    /// <summary>Label on the save button, e.g. "Добавить товар" or "Сохранить изменения".</summary>
    string SaveText { get; }

    /// <summary>Runs the form's validation and save. A failure leaves the sheet open.</summary>
    IAsyncRelayCommand SaveCommand { get; }

    /// <summary>Abandons the form. The host ViewModel turns this into closing the sheet.</summary>
    IRelayCommand CancelCommand { get; }

    /// <summary>Why the last save attempt was rejected. Empty when there is nothing to report.</summary>
    string ValidationMessage { get; }

    /// <summary>True when <see cref="ValidationMessage"/> has something to show.</summary>
    bool HasValidationMessage { get; }
}
