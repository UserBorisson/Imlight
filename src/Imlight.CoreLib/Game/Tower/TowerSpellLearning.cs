using System;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tower;

internal static class TowerSpellLearning {
    public static void Reconcile(Wizard wizard, TowerEconomyCollection economy, Action<Imcodec.MessageLayer.IMessage> send) {
        foreach (var id in economy.ReconcilePrimarySpells(wizard.CharId)) {
            if (wizard.SpellbookBehavior.HasSpell(id)) continue;
            var spell = SpellFactory.GetSpell(id);
            if (spell is null) continue;
            wizard.SpellbookBehavior.AddSpellToBook(spell);
            send(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int)id });
            send(new WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE {
                SpellID = id, DisplayText = "WizTraining_00000040", Success = 1,
            });
        }
    }
}

