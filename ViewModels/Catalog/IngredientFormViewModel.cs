using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Ingredient (stock position) create/edit form.</summary>
public class IngredientFormViewModel : ObservableObject, IFormFooterSource
{
    private readonly ICatalogService catalog;
    private readonly ILogger<IngredientFormViewModel> logger;
    private Guid? editingId;
    private bool loadedIsAvailable = true;

    public IngredientFormViewModel(ICatalogService catalog, ILogger<IngredientFormViewModel> logger)
    {
        this.catalog = catalog;
        this.logger = logger;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
    }

    public event Action? Saved;
    public event Action? CancelRequested;

    public bool IsEditing => editingId.HasValue;
    public string Title => IsEditing ? "Редактирование ингредиента" : "Новый ингредиент";
    public string SaveText => IsEditing ? "Сохранить изменения" : "Добавить ингредиент";

    private string name = string.Empty;
    public string Name { get => name; set => SetProperty(ref name, value); }

    private string unit = "г";
    public string Unit { get => unit; set => SetProperty(ref unit, value); }

    private string costText = string.Empty;
    public string CostText { get => costText; set => SetProperty(ref costText, value); }

    /// <summary>
    /// The cost field's placeholder, naming the selected currency's unit.
    /// </summary>
    /// <remarks>
    /// Bound for the same reason as <c>ProductFormViewModel.PricePlaceholder</c>: this is where an
    /// operator types an amount, and the unit name tells them which money the till is in.
    /// </remarks>
    public string CostPlaceholder => $"Себестоимость за единицу, {Currencies.Default.MinorUnitName}";

    private string stockText = string.Empty;
    public string StockText { get => stockText; set => SetProperty(ref stockText, value); }

    private string minStockText = string.Empty;
    public string MinStockText { get => minStockText; set => SetProperty(ref minStockText, value); }

    private string supplier = string.Empty;
    public string Supplier { get => supplier; set => SetProperty(ref supplier, value); }

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
        loadedIsAvailable = true;
        Name = string.Empty;
        Unit = "г";
        CostText = string.Empty;
        StockText = string.Empty;
        MinStockText = string.Empty;
        Supplier = string.Empty;
        ValidationMessage = string.Empty;
        NotifyState();
    }

    public async Task LoadAsync(Guid id)
    {
        Reset();
        var ingredient = await catalog.GetIngredientAsync(id);
        if (ingredient is null)
        {
            ValidationMessage = "Ингредиент не найден.";
            return;
        }

        editingId = ingredient.Id;
        loadedIsAvailable = ingredient.IsAvailable;
        Name = ingredient.Name;
        Unit = ingredient.Unit;
        CostText = ingredient.CostPerUnit.ToString("0.##");
        StockText = ingredient.StockQuantity.ToString("0.###");
        MinStockText = ingredient.MinStockLevel.ToString("0.###");
        Supplier = ingredient.Supplier ?? string.Empty;
        NotifyState();
    }

    private async Task SaveAsync()
    {
        ValidationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Name)) { ValidationMessage = "Укажите название ингредиента."; return; }
        if (string.IsNullOrWhiteSpace(Unit)) { ValidationMessage = "Укажите единицу измерения."; return; }
        if (!TextFormat.TryParseDecimal(CostText, out var cost) || cost < 0) { ValidationMessage = "Введите корректную себестоимость."; return; }
        if (!TextFormat.TryParseDecimal(StockText, out var stock) || stock < 0) { ValidationMessage = "Введите корректный остаток."; return; }
        if (!TextFormat.TryParseDecimal(MinStockText, out var minStock) || minStock < 0) { ValidationMessage = "Введите корректный минимальный остаток."; return; }

        IsBusy = true;
        try
        {
            await catalog.SaveIngredientAsync(new Ingredient
            {
                Id = editingId ?? Guid.NewGuid(),
                Name = Name.Trim(),
                Unit = Unit.Trim(),
                CostPerUnit = cost,
                StockQuantity = stock,
                MinStockLevel = minStock,
                Supplier = string.IsNullOrWhiteSpace(Supplier) ? null : Supplier.Trim(),
                IsAvailable = loadedIsAvailable
            });
            Saved?.Invoke();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save the ingredient");
            ValidationMessage = UserMessages.Describe(exception, "Не удалось сохранить ингредиент");
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
