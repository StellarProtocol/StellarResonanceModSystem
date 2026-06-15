using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>Editor-facing CRUD for named custom themes. A custom theme is a
/// (name, base preset) pair; its colours come from the base preset plus sparse
/// overrides. Built-in presets are not listed here (they are read-only).</summary>
internal interface ICustomThemeStore
{
    IReadOnlyList<string> Names { get; }
    ThemePreset BasePresetOf(string name);
    void Create(string name, ThemePreset basePreset);
    void Rename(string oldName, string newName);
    void Delete(string name);
}
