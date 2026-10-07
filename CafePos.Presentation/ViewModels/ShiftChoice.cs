using CafePos.Core.Common;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>The shift to analyse. «Текущая» / «Завершённая» plus the local times. Whether the shift has an end time. Carried as data rather than re-derived from the name because the picker's default selection IS this flag: «Анализировать смену» opens on the shift still running, and falls back to the newest closed one only when nothing is open.</summary>

public sealed record ShiftChoice(Guid Id, string DisplayName, bool IsClosed);

