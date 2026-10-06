using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Category create/edit form.</summary>
public class CategoryFormViewModel : ObservableObject, IFormFooterSource
{
    private readonly ICatalogService catalog;
    private readonly ILogger<CategoryFormViewModel> logger;
    private Guid? editingId;

    public CategoryFormViewModel(ICatalogService catalog, ILogger<CategoryFormViewModel> logger)
    {
        this.catalog = catalog;
        this.logger = logger;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
    }

    public event Action? Saved;
    public event Action? CancelRequested;

    public bool IsEditing => editingId.HasValue;
    public string Title => IsEditing ? "Редактирование категории" : "Новая категория";
    public string SaveText => IsEditing ? "Сохранить изменения" : "Добавить категорию";

    private string name = string.Empty;
    public string Name { get => name; set => SetProperty(ref name, value); }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set { if (SetProperty(ref validationMessage, value)) OnPropertyChanged(nameof(HasValidationMessage)); }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public void Reset()
    {
        editingId = null;
        Name = string.Empty;
        ValidationMessage = string.Empty;
        NotifyState();
    }

    public async Task LoadAsync(Guid id)
    {
        Reset();
        var category = (await catalog.GetCategoriesAsync()).FirstOrDefault(item => item.Id == id);
        if (category is null)
        {
            ValidationMessage = "Категория не найдена.";
            return;
        }

        editingId = category.Id;
        Name = category.Name;
        NotifyState();
    }

    private async Task SaveAsync()
    {
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = "Укажите название категории.";
            return;
        }

        IsBusy = true;
        try
        {
            await catalog.SaveCategoryAsync(new Category { Id = editingId ?? Guid.NewGuid(), Name = Name.Trim() });
            Saved?.Invoke();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save the category");
            ValidationMessage = UserMessages.Describe(exception, "Не удалось сохранить категорию");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SaveText));
    }
}
