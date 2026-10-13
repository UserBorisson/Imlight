using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
namespace Imlight.CoreLib.Game.Tower;
/// <summary>Native trainer contract; indices refer to the stable exact-ID catalog, never a mutable page.</summary>
internal static class TowerNativeTraining {
    // The native controller schedules its own reinteraction and requests fresh
    // options with an empty service name and Reinteract=4. Keep its target alive.
    public static Imcodec.MessageLayer.Generated.WIZARD2_53_PROTOCOL.MSG_REINTERACT CreateRefreshRequest()
        => new() {ReinteractType=4};
    public static bool IsRefreshRequest(Imcodec.MessageLayer.Generated.QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC message)
        => message.GlobalID!=0 && message.ServiceName=="" && message.Reinteract==4;
    public static IReadOnlyList<ServiceOptionBase> ComposeHubOptions(IEnumerable<ServiceOptionBase> hub,
        IEnumerable<WizTrainingOption> training) => hub.Concat<ServiceOptionBase>(training).ToArray();
    private static readonly TowerSpellUnlock[] Catalog = TowerSpellProgression.Spells.OrderBy(s=>s.TemplateId).ToArray();
    public static string SelectInitialSchool(string primarySchool,int level,IEnumerable<uint> learned) {
        var known=learned.ToArray();
        return new[]{primarySchool,"Fire","Ice","Storm","Myth","Life","Death","Balance","Astral"}
            .Distinct().FirstOrDefault(s=>CreateOptions(primarySchool,s,level,known).Count>0) ?? primarySchool;
    }
    public static TowerSpellUnlock ResolveIndex(int index) => index >= 0 && index < Catalog.Length ? Catalog[index] : null;
    public static IReadOnlyList<WizTrainingOption> CreateOptions(string primarySchool, string selectedSchool, int level, IEnumerable<uint> learned) {
        return CreateOptions(primarySchool,selectedSchool,level,learned,orderBySchool:false);
    }
    public static IReadOnlyList<WizTrainingOption> CreateAllOptions(string primarySchool,int level,IEnumerable<uint> learned)
        => CreateOptions(primarySchool,null,level,learned,orderBySchool:true);
    private static IReadOnlyList<WizTrainingOption> CreateOptions(string primarySchool,string selectedSchool,int level,
        IEnumerable<uint> learned,bool orderBySchool) {
        var known=learned.ToHashSet();
        return Catalog.Select((spell,index)=>(spell,index))
            .Where(x=>selectedSchool is null || MatchesSchool(x.spell.School,selectedSchool))
            .Where(x=>!known.Contains(x.spell.TemplateId))
            .Where(x=>x.spell.School.Equals(primarySchool,StringComparison.OrdinalIgnoreCase)
                || (x.spell.SecondaryTrainable && (string.IsNullOrEmpty(x.spell.RequiredPrimarySchool)
                    || x.spell.RequiredPrimarySchool.Equals(primarySchool,StringComparison.OrdinalIgnoreCase))))
            .OrderBy(x=>orderBySchool ? SchoolOrder(x.spell.School) : 0)
            .ThenBy(x=>x.spell.RequiredLevel).ThenBy(x=>x.spell.Name)
            .Select(x=>new WizTrainingOption {
                m_serviceName="WizTrainingService",m_iconKey="Training",m_trainingIndex=x.index,
                m_spellName=x.spell.NativeSpellName,m_displayKey=x.spell.NativeDisplayKey,
                m_requiredLevel=x.spell.RequiredLevel,m_trainingCost=0,
                m_bCanTrain=!known.Contains(x.spell.TemplateId) && TowerSpellProgression.EligibleSecondary(x.spell,primarySchool,level,known),
            }).ToArray();
    }
    private static int SchoolOrder(string school) => school switch {
        "Fire"=>0,"Ice"=>1,"Storm"=>2,"Myth"=>3,"Life"=>4,"Death"=>5,"Balance"=>6,"Sun"=>7,"Star"=>8,
        _=>9,
    };
    public static bool MatchesSchool(string spellSchool,string selectedSchool)
        => selectedSchool.Equals("Astral",StringComparison.OrdinalIgnoreCase)
            ? spellSchool is "Sun" or "Star" : spellSchool.Equals(selectedSchool,StringComparison.OrdinalIgnoreCase);
}
