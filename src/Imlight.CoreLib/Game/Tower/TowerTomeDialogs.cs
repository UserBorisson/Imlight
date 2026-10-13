using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;
namespace Imlight.CoreLib.Game.Tower;
internal static class TowerTomeDialogs {
    public static ServiceMementoBase CreateResetConfirmation(string nameKey, string icon, List<ServiceOptionBase> options)
        => new() {
            m_npcNameKey = nameKey, m_npcIcon = icon, m_npcTextKey = "NPCFormats_Name", m_serviceOptions = options,
            m_personaMadlibs = new MadlibBlock {
                m_blockToken = "NPC", m_madlibs = [new MadlibArgT_WideByteString {
                    m_madlibToken = "NAME",
                    m_madlibArgument = "Are you sure you want to reset Tower floor progress? Tickets, level, learned spells and equipment will be preserved. First-clear bonuses cannot be earned again.",
                }],
            },
        };
}
