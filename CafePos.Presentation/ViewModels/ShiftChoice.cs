using CafePos.Core.Common;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>The shift to analyse.</summary>
/// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

public sealed record ShiftChoice(Guid Id, string DisplayName, bool IsClosed);

