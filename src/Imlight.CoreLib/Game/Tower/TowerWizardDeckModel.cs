using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Tower;

public sealed record TowerWizardSpellCandidate(uint TemplateId, string School, int RequiredLevel,
    int PipRank, bool Supported);
public sealed record TowerWizardCard(uint TemplateId, uint Copies);

/// <summary>Finite school decks selected from an explicit learned-spell catalog.</summary>
public static class TowerWizardDeckModel {
    public static IReadOnlyList<TowerWizardCard> Select(string school, int level,
        IEnumerable<TowerWizardSpellCandidate> candidates) {
        var starter = school.ToLowerInvariant() switch {
            "fire" => 103007158U, "ice" => 1496157882U, "storm" => 2062265892U,
            "myth" => 1067010286U, "life" => 84361U, "death" => 1731857280U,
            "balance" => 2143810477U,
            _ => throw new ArgumentException("A native wizard school is required.", nameof(school)),
        };
        level = Math.Clamp(level, 1, TowerDifficultyModel.MaximumLevel);
        var maximumRank = Math.Min(14, 1 + level / 4);
        var copies = level < 50 ? 4U : 6U;
        var maximumEntries = level < 10 ? 4 : 10;
        var cards = new List<TowerWizardCard> { new(starter, 4) };
        cards.AddRange(candidates.Where(candidate => candidate.TemplateId != 0
                && candidate.TemplateId != starter && candidate.Supported
                && TowerSpellProgression.EligibleEnemy(candidate.TemplateId, school, level)
                && candidate.PipRank >= 0 && candidate.PipRank <= maximumRank
                && string.Equals(candidate.School, school, StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => candidate.TemplateId)
            .Select(group => group.OrderBy(candidate => candidate.RequiredLevel).First())
            .OrderByDescending(candidate => TowerSpellProgression.Find(candidate.TemplateId).RequiredLevel)
            .ThenBy(candidate => candidate.TemplateId)
            .Take(maximumEntries - 1)
            .Select(candidate => new TowerWizardCard(candidate.TemplateId, copies)));
        return cards;
    }
}
