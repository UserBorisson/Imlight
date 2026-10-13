using System;
using System.Collections.Generic;
using System.Linq;
namespace Imlight.CoreLib.Game.Tower;
public static class TowerTomePresentation {
    public static string ProgressSummary(string school, long climbHighest, TowerClearHistory history, IEnumerable<uint> learned) {
        var known = learned.ToHashSet();
        var next = TowerSpellProgression.Spells.Where(s => s.School.Equals(school, StringComparison.OrdinalIgnoreCase)
            && s.MilestoneFloor > 0 && !known.Contains(s.TemplateId) && history?.Contains(s.MilestoneFloor) != true)
            .OrderBy(s => s.MilestoneFloor).FirstOrDefault();
        return $"climb highest clear {climbHighest}, permanent highest clear {history?.Ranges.LastOrDefault()?.Last ?? 0}. "
            + (next is null ? "Utility milestones complete." : $"Next utility milestone: floor {next.MilestoneFloor}, {next.Name} (also level {next.RequiredLevel}).");
    }
    public static TowerItemStats AggregateAfter(TowerItemStats total, TowerItemStats before, TowerItemStats after)
        => new(total.Health + after.Health - before.Health, total.Mana + after.Mana - before.Mana,
            total.Damage + after.Damage - before.Damage, total.Accuracy + after.Accuracy - before.Accuracy,
            total.Resistance + after.Resistance - before.Resistance, total.Healing + after.Healing - before.Healing);
}
