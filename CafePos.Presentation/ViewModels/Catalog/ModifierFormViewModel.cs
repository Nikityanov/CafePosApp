using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Modifier group create/edit form. Options are edited as text lines (one option per line).</summary>
public class ModifierFormViewModel : ObservableObject, IFormFooterSource
{
    private readonly ICatalogService catalog;
    private readonly ILogger<ModifierFormViewModel> logger;
    private Guid? editingId;
    private List<ModifierOption> existingOptions = [];

    public ModifierFormViewModel(ICatalogService catalog, ILogger<ModifierFormViewModel> logger)
    {
        this.catalog = catalog;
        this.logger = logger;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
    }

    public event Action? Saved;
    public event Action? CancelRequested;

    public bool IsEditing => editingId.HasValue;
    public string Title => IsEditing ? "Редактирование группы" : "Новая группа модификаторов";
    public string SaveText => IsEditing ? "Сохранить изменения" : "Добавить группу";

    private string name = string.Empty;
    public string Name { get => name; set => SetProperty(ref name, value); }

    private string optionsText = string.Empty;
    public string OptionsText { get => optionsText; set => SetProperty(ref optionsText, value); }

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
        existingOptions = [];
        Name = string.Empty;
        OptionsText = string.Empty;
        ValidationMessage = string.Empty;
        NotifyState();
    }

    public async Task LoadAsync(Guid id)
    {
        Reset();
        var group = await catalog.GetModifierGroupAsync(id);
        if (group is null)
        {
            ValidationMessage = "Группа не найдена.";
            return;
        }

        editingId = group.Id;
        existingOptions = group.Options;
        Name = group.Name;
        OptionsText = string.Join(Environment.NewLine, group.Options.Select(option => option.Name));
        NotifyState();
    }

    private async Task SaveAsync()
    {
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = "Укажите название группы.";
            return;
        }

        var names = OptionsText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();

        if (names.Count == 0)
        {
            ValidationMessage = "Укажите хотя бы один вариант (каждый — с новой строки).";
            return;
        }

        IsBusy = true;
        try
        {
            var groupId = editingId ?? Guid.NewGuid();
            var group = new ModifierGroup
            {
                Id = groupId,
                Name = Name.Trim(),
                Options = [.. names.Select(optionName =>
                {
                    var existing = existingOptions.FirstOrDefault(option =>
                        string.Equals(option.Name, optionName, StringComparison.OrdinalIgnoreCase));
                    return new ModifierOption
                    {
                        Id = existing?.Id ?? Guid.NewGuid(),
                        Name = existing?.Name ?? optionName,
                        IsAvailable = existing?.IsAvailable ?? true,
                        ModifierGroupId = groupId
                    };
                })]
            };

            await catalog.SaveModifierGroupAsync(group);
            Saved?.Invoke();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save the modifier group");
            ValidationMessage = UserMessages.Describe(exception, "Не удалось сохранить группу");
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
