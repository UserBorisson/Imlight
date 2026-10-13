using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Imlight.CoreLib.Game.Tower;

public sealed record TowerSpellUnlock(uint TemplateId, string Name, string School, int RequiredLevel,
    long MilestoneFloor, bool SecondaryTrainable, uint RequiredSpellId, string Role, string NativePath, string RequiredPrimarySchool = "");

/// <summary>Exact learned variants. Native level metadata alone never authorizes a Tower spell.</summary>
public static class TowerSpellProgression {
    private sealed record Catalog(int SchemaVersion, string SourceClientRevision, TowerSpellUnlock[] Spells);
    private static readonly Lazy<IReadOnlyDictionary<uint, TowerSpellUnlock>> s_catalog = new(Load);
    public static IReadOnlyCollection<TowerSpellUnlock> Spells => s_catalog.Value.Values.ToArray();
    public static TowerSpellUnlock Find(uint templateId)
        => s_catalog.Value.TryGetValue(templateId, out var spell) ? spell : null;
    public static bool EligiblePrimary(TowerSpellUnlock spell, string school, int level, TowerClearHistory history)
        => spell is not null && string.Equals(spell.School, school, StringComparison.OrdinalIgnoreCase)
            && (level >= spell.RequiredLevel || (spell.MilestoneFloor > 0 && history?.Contains(spell.MilestoneFloor) == true));
    public static bool EligibleSecondary(TowerSpellUnlock spell, string school, int level, IEnumerable<uint> learned)
        => spell is not null && spell.SecondaryTrainable
            && !string.Equals(spell.School, school, StringComparison.OrdinalIgnoreCase)
            && level >= spell.RequiredLevel
            && (string.IsNullOrEmpty(spell.RequiredPrimarySchool) || string.Equals(spell.RequiredPrimarySchool, school, StringComparison.OrdinalIgnoreCase))
            && (spell.RequiredSpellId == 0 || learned.Contains(spell.RequiredSpellId));
    public static bool EligibleEnemy(uint templateId, string school, int level) {
        var spell = Find(templateId);
        level = Math.Clamp(level, 1, TowerDifficultyModel.MaximumLevel);
        return spell is not null && string.Equals(spell.School, school, StringComparison.OrdinalIgnoreCase)
            && (level >= spell.RequiredLevel || (spell.MilestoneFloor > 0 && level > spell.MilestoneFloor));
    }
    private static IReadOnlyDictionary<uint, TowerSpellUnlock> Load() {
        using var stream = typeof(TowerSpellProgression).Assembly.GetManifestResourceStream(
            "Imlight.CoreLib.Game.Tower.tower-spell-progression.json")
            ?? throw new InvalidOperationException("Missing Tower spell catalog.");
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Invalid Tower spell catalog.");
        if (catalog.SchemaVersion != 1 || catalog.Spells.Any(s => s.TemplateId == 0 || s.RequiredLevel < 1))
            throw new InvalidOperationException("Invalid Tower spell unlock.");
        return catalog.Spells.ToDictionary(s => s.TemplateId);
    }
}
