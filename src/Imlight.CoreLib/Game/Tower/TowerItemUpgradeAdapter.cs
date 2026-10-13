using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Tower;

public sealed record TowerItemUpgradePreview(bool Eligible, string Reason, string School,
    int CurrentTier, int DestinationTier, int Cost, TowerItemStats Before, TowerItemStats After,
    TowerItemStats UpgradeStats, int LegacyCreditUsed);

internal static class TowerItemUpgradeAdapter {
    internal static TowerItemUpgradePreview Preview(WizItemTemplate template, string wizardSchool, int level,
        TowerClearHistory history, TowerItemUpgradeState prior, int legacySpent) {
        var school = prior?.UpgradeSchool ?? wizardSchool;
        var restricted = template?.m_school;
        if (Enum.TryParse<MagicSchool>(restricted, true, out var nativeSchool) && nativeSchool != MagicSchool.None) {
            if (!string.Equals(nativeSchool.ToString(), school, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(nativeSchool.ToString(), wizardSchool, StringComparison.OrdinalIgnoreCase))
                return Rejected("This item's native school restriction does not match.");
            school = nativeSchool.ToString();
        }
        var slot = ItemHelper.GetItemSlot(template)?.SlotType.ToString();
        if (!TowerItemUpgradeModel.Slots.Contains(slot)) return Rejected("Only equipped combat gear can be upgraded.");
        var native = ReadNativeStats(template, school);
        // Legacy paid universal damage remains intact and contributes to the target gap.
        native += new TowerItemStats(0, 0, prior?.DamageBonusPercent ?? 0, 0, 0, 0);
        var current = prior?.SchemaVersion >= 2 ? prior.Tier : 0;
        var oldDelta = prior?.SchemaVersion >= 2 ? prior.Stats : TowerItemStats.Zero;
        var before = native + oldDelta;
        var maximum = TowerItemUpgradeModel.MaximumEligibleTier(level, history);
        for (var tier = current + 1; tier <= maximum; tier++) {
            var delta = TowerItemUpgradeModel.Gap(native, TowerItemUpgradeModel.GetTarget(school, slot, tier));
            if (delta == oldDelta) continue;
            var unusedCredit = Math.Max(0, legacySpent - (prior?.LegacyCreditUsed ?? 0));
            var credit = Math.Min(unusedCredit, TowerItemUpgradeModel.Cost(tier));
            return new(true, "", school, current, tier, TowerItemUpgradeModel.Cost(tier) - credit,
                before, native + delta, delta, (prior?.LegacyCreditUsed ?? 0) + credit);
        }
        return new(false, "No stronger target unlocked; gain levels and clear Tower floors.", school,
            current, current, 0, before, before, oldDelta, prior?.LegacyCreditUsed ?? 0);
    }
    private static TowerItemUpgradePreview Rejected(string reason)
        => new(false, reason, "", 0, 0, 0, TowerItemStats.Zero, TowerItemStats.Zero, TowerItemStats.Zero, 0);
    internal static TowerItemStats ReadNativeStats(WizItemTemplate template, string school) {
        var stats = new ServerWizGameStats(Enum.Parse<MagicSchool>(school), 1) { m_baseHitpoints = 0, m_baseMana = 0 };
        CharacterEffectHelper.AddEffectsToGameStats(stats, template);
        var index = (int)MagicSchools.GetMagicSchool(school).m_schoolIndex;
        float At(List<float> values) => values is not null && values.Count > index ? values[index] : 0;
        return new(stats.m_baseHitpoints, stats.m_baseMana,
            stats.m_dmgBonusPercentAll + At(stats.m_dmgBonusPercent),
            stats.m_accBonusPercentAll + At(stats.m_accBonusPercent),
            stats.m_dmgReducePercentAll, stats.m_healBonusPercentAll);
    }
    internal static IEnumerable<(string Name, WizStatisticEffect Effect)> Effects(TowerItemUpgradeState state) {
        if (state.DamageBonusPercent > 0)
            yield return ("CanonicalAllDamage", Encode("CanonicalAllDamage", new WizStatisticEffect {m_damageBonusPercent=state.DamageBonusPercent}, state.DamageBonusPercent));
        if (state.SchemaVersion < 2 || state.Stats is null) yield break;
        var stats=state.Stats;
        if (stats.Health > 0) yield return ("CanonicalMaxHealth", Encode("CanonicalMaxHealth", new WizStatisticEffect {m_hitPointBonus=stats.Health}, stats.Health));
        if (stats.Mana > 0) yield return ("CanonicalMaxMana", Encode("CanonicalMaxMana", new WizStatisticEffect {m_manaBonus=stats.Mana}, stats.Mana));
        if (stats.Healing > 0) yield return ("CanonicalLifeHealing", Encode("CanonicalLifeHealing", new WizStatisticEffect {m_healBonusPercent=stats.Healing}, stats.Healing));
        if (stats.Damage > 0) yield return ($"Canonical{state.UpgradeSchool}Damage", Encode($"Canonical{state.UpgradeSchool}Damage", new WizStatisticEffect {m_damageBonusPercent=stats.Damage}, stats.Damage));
        if (stats.Accuracy > 0) yield return ($"Canonical{state.UpgradeSchool}Accuracy", Encode($"Canonical{state.UpgradeSchool}Accuracy", new WizStatisticEffect {m_accuracyBonusPercent=stats.Accuracy}, stats.Accuracy));
        if (stats.Resistance > 0) yield return ("CanonicalAllReduceDamage", Encode("CanonicalAllReduceDamage", new WizStatisticEffect {m_damageReducePercent=stats.Resistance}, stats.Resistance));
    }
    private static WizStatisticEffect Encode(string name, WizStatisticEffect effect, float value) {
        effect.m_lookupIndex = CanonicalStatEffects.GetLookupIndex(name, value);
        return effect;
    }
}
