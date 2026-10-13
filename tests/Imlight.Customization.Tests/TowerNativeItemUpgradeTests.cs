using System.Collections.Concurrent;
using System.Reflection;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

internal static class TowerNativeItemUpgradeTests {
    public static void Run() {
        var core = typeof(TowerRunState).Assembly;
        foreach (var name in new[]{"GameEffectRuleData","CanonicalStatEffects"})
            Activator.CreateInstance(core.GetType("Imlight.CoreLib.Game.Effects."+name)!);
        var effects = core.GetType("Imlight.CoreLib.Game.Tower.TowerItemUpgradeAdapter")!
            .GetMethod("Effects",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var school in new[]{"Fire","Ice","Storm","Myth","Life","Death","Balance"})
        foreach(var slot in TowerItemUpgradeModel.Slots)
        foreach(var tier in new[]{1,2,6,TowerItemUpgradeModel.MaximumTier}) {
            var state = new TowerItemUpgradeState {SchemaVersion=2, UpgradeSchool=school,
                Stats=TowerItemUpgradeModel.GetTarget(school,slot,tier)};
            _ = ((IEnumerable<(string,WizStatisticEffect)>)effects.Invoke(null,[state])!).ToArray();
        }
        const uint templateId = 4000000001;
        const ulong wizardId = 718181;
        const ulong itemId = 818181;
        var template = new WizItemTemplate {m_templateID=templateId, m_school="Fire", m_adjectiveList=["Hat"], m_equipEffects=[]};
        var cache = (ConcurrentDictionary<ulong,CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        cache[templateId] = template;
        var database="LocalTowerItemTests-"+Guid.NewGuid().ToString("N");
        using var store=new DocumentStore{Urls=["http://127.0.0.1:8180"],Database=database}.Initialize();
        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));
        try {
            using(var session=store.OpenSession()) {
                var wizard=JObject.FromObject(new {CharId=wizardId, MagicSchoolBehavior=new {MagicSchool="Fire",Level=1},
                    EquipmentBehavior=new {EquippedItemIds=new[]{itemId}}, GameStats=new {m_currentPvPCurrency=100},
                    SpellbookBehavior=new {LearnedSpellTemplateIds=Array.Empty<uint>()}});
                session.Store(wizard,"fixtures/wizard");session.Advanced.GetMetadataFor(wizard)["@collection"]="Wizards";
                var item=new WizClientObjectItem{m_globalID=itemId,m_templateID=templateId};
                session.Store(item,"fixtures/item");session.Advanced.GetMetadataFor(item)["@collection"]="WizardItems";
                session.SaveChanges();
            }
            var economy=new TowerEconomyCollection(store);
            var preview=economy.GetEquippedUpgradePreview(wizardId,itemId)!;
            if(!preview.Eligible || preview.Cost!=10 || preview.After.Health<=0)throw new Exception("Native equipped preview missing.");
            var attempts=Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(
                ()=>economy.UpgradeEquippedItem(wizardId,itemId,templateId,0,10)))).GetAwaiter().GetResult();
            var result=attempts.Single(r=>r.Status==TowerPurchaseStatus.Purchased);
            if(result.Status!=TowerPurchaseStatus.Purchased || result.TicketsAfter!=90 || result.Upgrade is null)
                throw new Exception("Native target purchase failed: "+result.Status);
            if(economy.UpgradeEquippedItem(wizardId,itemId,templateId,0,10).Status==TowerPurchaseStatus.Purchased
                || economy.GetTicketBalance(wizardId)!=90)throw new Exception("Duplicate purchase charged twice.");
            using(var changed=store.OpenSession()) {
                var savedWizard=changed.Load<JObject>("fixtures/wizard")!;
                savedWizard["EquipmentBehavior"]!["EquippedItemIds"] = new JArray();
                changed.SaveChanges();
            }
            if(economy.GetEquippedUpgradePreview(wizardId,itemId) is not null)
                throw new Exception("Unequipped item still offers purchases.");
            var live=new Wizard {CharId=wizardId, GameStats=new ServerWizGameStats(MagicSchool.Fire,1),
                SpellbookBehavior=new ServerWizSpellbookBehavior(), TowerItemUpgrades=new TowerItemUpgradeBook{ItemUpgrades=[result.Upgrade]}};
            live.GameStats.m_baseHitpoints=100;
            var helper=core.GetType("Imlight.CoreLib.Game.Effects.CharacterEffectHelper")!;
            var add=helper.GetMethod("AddEffectsToWizard",BindingFlags.NonPublic|BindingFlags.Static)!;
            var remove=helper.GetMethod("RemoveEffectsFromWizard",BindingFlags.NonPublic|BindingFlags.Static)!;
            add.Invoke(null,[live,template,itemId]);
            var nativeSnapshot=live.GameStats.GetClientTypeAlternative();
            if(nativeSnapshot.m_basePvPCurrencyPouch!=int.MaxValue)
                throw new Exception("Native relog stats omit Arena wallet capacity.");
            var health=live.GameStats.m_baseHitpoints;
            add.Invoke(null,[live,template,itemId]);
            if(live.GameStats.m_baseHitpoints!=health)throw new Exception("Repeated upgrade hydration stacks health.");
            remove.Invoke(null,[live,template,itemId]);
            if(live.GameStats.m_baseHitpoints!=100 || live.GameEffects.Count!=0)throw new Exception("Unequip leaves upgrade effects.");
            live.EquipmentBehavior=new ServerWizEquipmentBehavior {EquippedItemIds=[],EquippedItems=[],SlotList=[]};
            live.EquipmentBehavior.ForceEquipItem(new WizClientObjectItem {m_globalID=itemId,m_templateID=templateId});
            live.PlayerNameBehavior=new ServerWizPlayerNameBehavior {NameOverride="Equipment Test"};
            live.MagicSchoolBehavior=new ServerMagicSchoolBehavior {MagicSchool=MagicSchool.Fire,Level=1};
            var recalculate=core.GetType("Imlight.CoreLib.Shared.Character.CharacterHelper")!
                .GetMethod("RecalculateGameStats",BindingFlags.NonPublic|BindingFlags.Static)!;
            recalculate.Invoke(null,[live]);
            var rebuiltHealth=live.GameStats.m_baseHitpoints;
            var rebuiltDamage=live.GameStats.m_dmgBonusPercent?.ToArray();
            recalculate.Invoke(null,[live]);
            if(live.GameStats.m_baseHitpoints!=rebuiltHealth
                || !(live.GameStats.m_dmgBonusPercent ?? []).SequenceEqual(rebuiltDamage ?? []))
                throw new Exception("Repeated full stat recalculation stacks upgrade contributions.");
            using var reload=store.OpenSession();
            var saved=reload.Load<JObject>("fixtures/wizard")!["TowerItemUpgrades"]!.ToObject<TowerItemUpgradeBook>()!;
            if(saved.ItemUpgrades.Single().Stats!=result.Upgrade.Stats)throw new Exception("Item target not durable.");
            Console.WriteLine("PASS: native item preview/purchase, retry, effect hydration, unequip and persisted targets");
        }
        finally {store.Maintenance.Server.Send(new DeleteDatabasesOperation(database,hardDelete:true));cache.TryRemove(templateId,out _);}
    }
}

