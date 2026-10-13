using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Imlight.CoreLib.Game.Tower;

public sealed record TowerItemStats(int Health, int Mana, float Damage, float Accuracy, float Resistance, float Healing) {
    public static TowerItemStats Zero { get; } = new(0, 0, 0, 0, 0, 0);
    public static TowerItemStats operator +(TowerItemStats a, TowerItemStats b)
        => new(a.Health+b.Health, a.Mana+b.Mana, a.Damage+b.Damage, a.Accuracy+b.Accuracy,
            a.Resistance+b.Resistance, a.Healing+b.Healing);
}

/// <summary>Versioned school/slot ordinary stat targets; no card, socket or pip mutations.</summary>
public static class TowerItemUpgradeModel {
    private sealed record Anchor(int Level, double[] Multipliers);
    private sealed record Targets(int SchemaVersion, Dictionary<string, double[]> SchoolBudgets,
        Dictionary<string, double[]> SlotWeights, Anchor[] Anchors);
    private static readonly Lazy<Targets> s_targets = new(Load);
    public static IReadOnlyCollection<string> Slots => s_targets.Value.SlotWeights.Keys;
    public static int MaximumTier => 2 + (TowerDifficultyModel.MaximumLevel - 1) / 10;
    public static int TargetLevel(int tier) {
        if (tier < 1 || tier > MaximumTier) throw new ArgumentOutOfRangeException(nameof(tier));
        return tier == 1 ? 1 : Math.Min((tier - 1) * 10, TowerDifficultyModel.MaximumLevel);
    }
    public static int Cost(int tier) { _ = TargetLevel(tier); return checked(10 * tier); }
    public static int MaximumEligibleTier(int level, TowerClearHistory history) {
        var highest = history?.Ranges.LastOrDefault()?.Last ?? 0;
        return Enumerable.Range(1, MaximumTier).LastOrDefault(tier => TargetLevel(tier) <= level
            && TargetLevel(tier) - 1 <= highest);
    }
    public static TowerItemStats GetTarget(string school, string slot, int tier) {
        var data = s_targets.Value;
        if (!data.SchoolBudgets.TryGetValue(school, out var budget)
            || !data.SlotWeights.TryGetValue(slot, out var weights))
            throw new ArgumentException("A supported wizard school and combat slot are required.");
        var level = TargetLevel(tier);
        var upper = Array.FindIndex(data.Anchors, anchor => anchor.Level >= level);
        if (upper < 0) upper = data.Anchors.Length - 1;
        var low = data.Anchors[Math.Max(0, upper - 1)];
        var high = data.Anchors[upper];
        var fraction = high.Level == low.Level ? 0 : (double)(level - low.Level) / (high.Level - low.Level);
        var values = Enumerable.Range(0, 6).Select(i => budget[i] * weights[i] / 100
            * (low.Multipliers[i] + fraction * (high.Multipliers[i] - low.Multipliers[i]))).ToArray();
        return new((int)Math.Round(values[0]), (int)Math.Round(values[1]), (float)Math.Round(values[2]/100, 2),
            (float)Math.Round(values[3]/100, 2), (float)Math.Round(values[4]/100, 2), (float)Math.Round(values[5]/100, 2));
    }
    public static TowerItemStats Gap(TowerItemStats native, TowerItemStats target)
        => new(Math.Max(0,target.Health-native.Health),Math.Max(0,target.Mana-native.Mana),
            Math.Max(0,target.Damage-native.Damage),Math.Max(0,target.Accuracy-native.Accuracy),
            Math.Max(0,target.Resistance-native.Resistance),Math.Max(0,target.Healing-native.Healing));
    private static Targets Load() {
        using var stream = typeof(TowerItemUpgradeModel).Assembly.GetManifestResourceStream(
            "Imlight.CoreLib.Game.Tower.tower-item-upgrade-targets.json")
            ?? throw new InvalidOperationException("Missing Tower item stat targets.");
        var data = JsonSerializer.Deserialize<Targets>(stream, new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
        if (data?.SchemaVersion != 1 || data.SchoolBudgets.Count != 7 || data.SlotWeights.Count != 8)
            throw new InvalidOperationException("Invalid Tower item stat targets.");
        return data;
    }
}
