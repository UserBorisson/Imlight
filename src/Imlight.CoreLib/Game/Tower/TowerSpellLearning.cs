using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tower;

internal static class TowerSpellLearning {
    public static void Reconcile(Wizard wizard, TowerEconomyCollection economy, Action<Imcodec.MessageLayer.IMessage> send) {
        var acquired = new List<uint>();
        foreach (var id in economy.ReconcilePrimarySpells(wizard.CharId)) {
            if (wizard.SpellbookBehavior.HasSpell(id)) continue;
            var spell = SpellFactory.GetSpell(id);
            if (spell is null) continue;
            wizard.SpellbookBehavior.AddSpellToBook(spell);
            send(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int)id });
            acquired.Add(id);
        }
        if (acquired.Count > 0) send(CreateAcquisitionPopup(acquired));
    }
    internal static WIZARD_12_PROTOCOL.MSG_QUESTREWARDS CreateAcquisitionPopup(IEnumerable<uint> spellIds) {
        var loot = new LootInfoList {m_loot = spellIds.Distinct().Select(id => (LootInfo)new AddSpellLootInfo {
            m_lootType = LOOT_TYPE.LOOT_TYPE_ADD_SPELL, m_spellID = id,
        }).ToList()};
        if (!new ObjectSerializer(Versionable:false).Serialize(loot, 1, out var data))
            throw new InvalidOperationException("Cannot serialize native learned-spell popup.");
        return new WIZARD_12_PROTOCOL.MSG_QUESTREWARDS {QuestID=0, LootList=data};
    }
}

