using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
namespace Imlight.CoreLib.Game.Tower;
/// <summary>Native trainer contract; indices refer to the stable exact-ID catalog, never a mutable page.</summary>
internal static class TowerNativeTraining {
    // Native OpenNPCOptions mode 2 only reopens selected service types; mode 4
    // additionally reopens the active target for arbitrary replacement services.
    public const int ImmediateRefresh=4;
    public static IReadOnlyList<ServiceOptionBase> ComposeHubOptions(IEnumerable<ServiceOptionBase> hub,
        IEnumerable<WizTrainingOption> training) => hub.Concat<ServiceOptionBase>(training).ToArray();
    private static readonly TowerSpellUnlock[] Catalog = TowerSpellProgression.Spells.OrderBy(s=>s.TemplateId).ToArray();
    public static TowerSpellUnlock ResolveIndex(int index) => index >= 0 && index < Catalog.Length ? Catalog[index] : null;
    public static IReadOnlyList<WizTrainingOption> CreateOptions(string primarySchool, string selectedSchool, int level, IEnumerable<uint> learned) {
        var known=learned.ToHashSet();
        return Catalog.Select((spell,index)=>(spell,index))
            .Where(x=>MatchesSchool(x.spell.School,selectedSchool))
            .Where(x=>x.spell.School.Equals(primarySchool,StringComparison.OrdinalIgnoreCase)
                || (x.spell.SecondaryTrainable && (string.IsNullOrEmpty(x.spell.RequiredPrimarySchool)
                    || x.spell.RequiredPrimarySchool.Equals(primarySchool,StringComparison.OrdinalIgnoreCase))))
            .OrderBy(x=>x.spell.RequiredLevel).ThenBy(x=>x.spell.Name)
            .Select(x=>new WizTrainingOption {
                m_serviceName="WizTrainingService",m_iconKey="Training",m_trainingIndex=x.index,
                m_spellName=x.spell.NativeSpellName,m_displayKey=x.spell.NativeDisplayKey,
                m_requiredLevel=x.spell.RequiredLevel,m_trainingCost=0,
                m_bCanTrain=!known.Contains(x.spell.TemplateId) && TowerSpellProgression.EligibleSecondary(x.spell,primarySchool,level,known),
            }).ToArray();
    }
    public static bool MatchesSchool(string spellSchool,string selectedSchool)
        => selectedSchool.Equals("Astral",StringComparison.OrdinalIgnoreCase)
            ? spellSchool is "Sun" or "Star" : spellSchool.Equals(selectedSchool,StringComparison.OrdinalIgnoreCase);
}
