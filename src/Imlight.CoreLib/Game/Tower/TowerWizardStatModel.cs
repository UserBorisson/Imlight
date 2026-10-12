using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Game.Tower;

public sealed record TowerWizardEquipmentTier(int Level, double HealthFraction, int ManaBonus,
    double DamageBonus, double AccuracyBonus, double ResistanceBonus, double PowerPipBonus,
    double HealingBonus, byte StartingPips, byte StartingPowerPips);

/// <summary>Applies invisible expected equipment to native wizard base stats, without using the player's gear.</summary>
public static class TowerWizardStatModel {
    private static readonly Lazy<TowerWizardEquipmentTier[]> s_tiers = new(LoadTiers);

    public static double GetNormalHealthFactor(int level) {
        level = Math.Clamp(level, 1, TowerDifficultyModel.MaximumLevel);
        var anchors = new (int Level, double Factor)[] {
            (1, .85), (10, .90), (30, 1.00), (50, 1.05), (100, 1.10),
            (TowerDifficultyModel.MaximumLevel, 1.15),
        };
        for (var i = 1; i < anchors.Length; i++) {
            if (level > anchors[i].Level) { continue; }
            var low = anchors[i - 1];
            var high = anchors[i];
            return low.Factor + (high.Factor - low.Factor)
                * (level - low.Level) / (high.Level - low.Level);
        }
        return anchors[^1].Factor;
    }

    public static TowerWizardEquipmentTier GetEquipment(int level) {
        level = Math.Clamp(level, 1, byte.MaxValue);
        var tiers = s_tiers.Value;
        var upper = Array.FindIndex(tiers, tier => tier.Level >= level);
        if (upper < 0) return tiers[^1];
        if (upper == 0 || tiers[upper].Level == level) return tiers[upper];
        var low = tiers[upper - 1];
        var high = tiers[upper];
        var fraction = (double)(level - low.Level) / (high.Level - low.Level);
        double Blend(double a, double b) => a + (b - a) * fraction;
        return new(level, Blend(low.HealthFraction, high.HealthFraction),
            (int)Math.Round(Blend(low.ManaBonus, high.ManaBonus)),
            Blend(low.DamageBonus, high.DamageBonus), Blend(low.AccuracyBonus, high.AccuracyBonus),
            Blend(low.ResistanceBonus, high.ResistanceBonus), Blend(low.PowerPipBonus, high.PowerPipBonus),
            Blend(low.HealingBonus, high.HealingBonus), low.StartingPips, low.StartingPowerPips);
    }

    public static void Apply(ServerWizGameStats stats, int schoolIndex, float healthMultiplier) {
        ArgumentNullException.ThrowIfNull(stats);
        if (schoolIndex is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(schoolIndex));
        if (!float.IsFinite(healthMultiplier) || healthMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(healthMultiplier));
        var equipment = GetEquipment(stats.Level);
        stats.m_baseHitpoints = (int)Math.Clamp(Math.Round(Math.Max(1, stats.m_baseHitpoints)
            * GetNormalHealthFactor(stats.Level) * healthMultiplier, MidpointRounding.AwayFromZero), 1d, int.MaxValue);
        stats.m_baseMana = (int)Math.Clamp((long)stats.m_baseMana + equipment.ManaBonus, 0L, int.MaxValue);
        stats.m_dmgBonusPercent = SchoolBonus(stats.m_dmgBonusPercent, schoolIndex, (float)equipment.DamageBonus);
        stats.m_accBonusPercent = SchoolBonus(stats.m_accBonusPercent, schoolIndex, (float)equipment.AccuracyBonus);
        stats.m_dmgReducePercentAll = (float)equipment.ResistanceBonus;
        stats.m_powerPipBase = Math.Clamp(stats.m_powerPipBase, 0f, 1f);
        stats.m_powerPipBonusPercentAll = Math.Min((float)equipment.PowerPipBonus, 1f - stats.m_powerPipBase);
        stats.m_healBonusPercentAll = stats.MagicSchool == MagicSchool.Life ? (float)equipment.HealingBonus : 0f;
        stats.m_startingPips = equipment.StartingPips;
        stats.m_startingPowerPips = equipment.StartingPowerPips;
        stats.m_currentHitpoints = stats.m_baseHitpoints;
        stats.m_currentMana = stats.m_baseMana;
        stats.m_referenceLevel = stats.Level;
    }

    private static List<float> SchoolBonus(List<float> values, int schoolIndex, float bonus) {
        values ??= [];
        while (values.Count < Math.Max(12, schoolIndex + 1)) values.Add(0f);
        values[schoolIndex] += bonus;
        return values;
    }

    private static TowerWizardEquipmentTier[] LoadTiers() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Imlight.CoreLib.Game.Tower.tower-wizard-equipment.json")
            ?? throw new InvalidOperationException("Tower wizard equipment profiles are missing.");
        var tiers = JsonSerializer.Deserialize<TowerWizardEquipmentTier[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (tiers.Length < 2 || tiers[0].Level != 1) throw new InvalidOperationException("Invalid Tower wizard equipment tiers.");
        var priorLevel = 0;
        foreach (var tier in tiers) {
            if (tier.Level <= priorLevel || tier.Level > byte.MaxValue
                || !double.IsFinite(tier.HealthFraction) || tier.HealthFraction is < 0 or > 2
                || tier.ManaBonus is < 0 or > 1000
                || !double.IsFinite(tier.DamageBonus) || tier.DamageBonus is < 0 or > 2
                || !double.IsFinite(tier.AccuracyBonus) || tier.AccuracyBonus is < 0 or > .3
                || !double.IsFinite(tier.ResistanceBonus) || tier.ResistanceBonus is < 0 or > .5
                || !double.IsFinite(tier.PowerPipBonus) || tier.PowerPipBonus is < 0 or > 1
                || !double.IsFinite(tier.HealingBonus) || tier.HealingBonus is < 0 or > .5
                || tier.StartingPips + tier.StartingPowerPips > 2)
                throw new InvalidOperationException($"Invalid Tower wizard equipment tier {tier.Level}.");
            priorLevel = tier.Level;
        }
        return tiers;
    }
}
