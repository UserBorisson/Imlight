/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * SCHOOL GIFT CAMPAIGN
 * ========================================================================
 * 
 * PURPOSE:
 * Provides persistent, manual school gift entitlements.
 * 
 * USAGE EXAMPLE:
 * Loaded only when Customization.ContentPath is configured.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nikola Borissov with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 10/11/2026
 */

using SchoolContentCompiler;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.WizardData.Collections;
using Imcodec.ObjectProperty.TypeCache;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.Exceptions;

internal static class GiftClaimIntegrationTests {
    private static readonly ulong WizardId = 4627075689524835696;
    public static void Run() {
        var database = "LocalSchoolGiftTests-" + Guid.NewGuid().ToString("N");
        using var store = new DocumentStore { Urls = ["http://127.0.0.1:8180"], Database = database, Conventions = { UseOptimisticConcurrency = true } }.Initialize();
        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));
        try {
            var catalog = ContentCatalog.Load(Path.GetFullPath("content"));
            var campaign = new SchoolGiftCampaign(catalog);
            ulong itemCounter = 5000000000000000000;
            WizClientObjectItem Factory(uint id) => new() { m_templateID = id, m_globalID = Interlocked.Increment(ref itemCounter) };
            SchoolGiftClaimCollection Repo(int capacity = 11, SchoolGiftCampaign overrideCampaign = null) => new(store,overrideCampaign ?? campaign,capacity,Factory);
            void Seed(ulong wizard, int count = 2, int school = 72777) {
                var doc = JObject.FromObject(new { AccountId = 123UL, CharId = wizard, MagicSchoolBehavior = new { MagicSchool = ((Imlight.CoreLib.Shared.Behaviors.MagicSchool)school).ToString() }, InventoryBehavior = new { InventoryItemIds = Enumerable.Range(0,count).Select(i => (ulong)(i+100)).ToArray() }, EquipmentBehavior = new { SlotList = new[] { new { ItemId = 999UL } }, EquippedItemIds = new ulong[] {999} }, PreserveUnknown = "unchanged" });
                using var session = store.OpenSession();
                session.Store(doc,"fixtures/" + wizard);
                session.Advanced.GetMetadataFor(doc)["@collection"] = "Wizards";
                session.Advanced.GetMetadataFor(doc)["Raven-Clr-Type"] = "Imlight.CoreLib.WizardData.Models.Player.Wizard, Imlight.CoreLib";
                session.SaveChanges();
            }
            JObject Load(ulong wizard) { using var s = store.OpenSession(); return s.Load<JObject>("fixtures/" + wizard); }
            void Expect(GiftClaimResult result, GiftClaimStatus expected) => GiftEligibilityTests.Check(result.Status == expected,$"Expected {expected}, got {result.Status}");
            Seed(WizardId);
            var before = Load(WizardId);
            GiftEligibilityTests.Check(before["CharId"].Value<ulong>() == WizardId,"exact 64-bit ID and JObject metadata preservation");
            Expect(Repo().TryClaim(124,WizardId,campaign.GiftId(WizardId)),GiftClaimStatus.NotEligible);
            Expect(Repo().TryClaim(123,WizardId,campaign.GiftId(WizardId+1)),GiftClaimStatus.InvalidGift);
            Expect(Repo(10).TryClaim(123,WizardId,campaign.GiftId(WizardId)),GiftClaimStatus.InventoryFull);
            GiftEligibilityTests.Check(JToken.DeepEquals(before,Load(WizardId)),"failure preserves complete wizard");
            var disabled = new SchoolGiftCampaign(catalog with { Campaign = catalog.Campaign with { Enabled = false } });
            Expect(Repo(11,disabled).TryClaim(123,WizardId,campaign.GiftId(WizardId)),GiftClaimStatus.NotEligible);
            var claimed = Repo().TryClaim(123,WizardId,campaign.GiftId(WizardId));
            Expect(claimed,GiftClaimStatus.Claimed);
            GiftEligibilityTests.Check(claimed.ItemIds.Count == 8 && claimed.ItemIds.Distinct().Count() == 8,"eight unique committed IDs");
            var after = Load(WizardId);
            GiftEligibilityTests.Check(after["InventoryBehavior"]["InventoryItemIds"].Count() == 10,"eight-slot boundary");
            GiftEligibilityTests.Check(after["InventoryBehavior"]["InventoryItemIds"].Take(2).Select(t=>t.Value<ulong>()).SequenceEqual(new ulong[]{100,101}),"original inventory preserved");
            GiftEligibilityTests.Check(JToken.DeepEquals(before["EquipmentBehavior"],after["EquipmentBehavior"]) && after["PreserveUnknown"].Value<string>() == "unchanged","original equipment and unknown fields preserved");
            var retried = Repo().TryClaim(123,WizardId,campaign.GiftId(WizardId));
            Expect(retried,GiftClaimStatus.AlreadyClaimed);
            GiftEligibilityTests.Check(retried.ItemIds.SequenceEqual(claimed.ItemIds),"lost response returns committed IDs");
            var next = new SchoolGiftCampaign(catalog with { ContentVersion = "next" });
            Expect(Repo(11,next).TryClaim(123,WizardId,campaign.GiftId(WizardId)),GiftClaimStatus.AlreadyClaimed);
            Seed(WizardId+1);
            var concurrent = new GiftClaimResult[2];
            Parallel.For(0,2,i=>concurrent[i]=Repo().TryClaim(123,WizardId+1,campaign.GiftId(WizardId+1)));
            GiftEligibilityTests.Check(concurrent.Count(r=>r.Status == GiftClaimStatus.Claimed) == 1 && concurrent.Count(r=>r.Status == GiftClaimStatus.AlreadyClaimed) == 1,"one concurrent winner");
            GiftEligibilityTests.Check(Load(WizardId+1)["InventoryBehavior"]["InventoryItemIds"].Count() == 10,"race adds only eight");
            Seed(WizardId+2);
            var calls=0;
            var broken = new SchoolGiftClaimCollection(store,campaign,11,id => { if (++calls == 5) { throw new InvalidOperationException("simulated factory failure"); } return Factory(id); });
            Expect(broken.TryClaim(123,WizardId+2,campaign.GiftId(WizardId+2)),GiftClaimStatus.RetryableFailure);
            GiftEligibilityTests.Check(Load(WizardId+2)["InventoryBehavior"]["InventoryItemIds"].Count() == 2,"failed transaction has no references");
            using(var s=store.OpenSession()) { GiftEligibilityTests.Check(s.Load<JObject>(campaign.ReceiptId(WizardId+2)) is null,"failed transaction has no receipt"); }
            Expect(Repo().TryClaim(123,WizardId+2,campaign.GiftId(WizardId+2)),GiftClaimStatus.Claimed);
            using(var s=store.OpenSession()) {
                var count = s.Advanced.RawQuery<JObject>("from WizardItems").WaitForNonStaleResults().ToList().Count;
                GiftEligibilityTests.Check(count == 24,"exact item documents after races and failures");
            }
            Seed(WizardId+3);
            var collisionId = $"school-gift-items/school-starter-v1/{WizardId+3}/6000000000009000009";
            using(var s=store.OpenSession()) { s.Store(new { Existing = true },collisionId); s.SaveChanges(); }
            var conflicts = new SchoolGiftClaimCollection(store,campaign,11,id => new WizClientObjectItem { m_templateID = id, m_globalID = 6000000000000000000UL+id });
            Expect(conflicts.TryClaim(123,WizardId+3,campaign.GiftId(WizardId+3)),GiftClaimStatus.RetryableFailure);
            GiftEligibilityTests.Check(Load(WizardId+3)["InventoryBehavior"]["InventoryItemIds"].Count() == 2,"server rejects whole conflicting transaction");
            using(var s=store.OpenSession()) {
                GiftEligibilityTests.Check(s.Load<JObject>(campaign.ReceiptId(WizardId+3)) is null,"server rollback preserves entitlement");
                GiftEligibilityTests.Check(s.Load<JObject>($"school-gift-items/school-starter-v1/{WizardId+3}/6000000000009000010") is null,"server rollback leaves no partial items");
            }
            foreach(var school in Enum.GetValues<School>()) {
                var wizard = WizardId + 100 + (ulong)school;
                Seed(wizard,school:(int)Enum.Parse<Imlight.CoreLib.Shared.Behaviors.MagicSchool>(school.ToString()));
                var result = Repo().TryClaim(123,wizard,campaign.GiftId(wizard));
                Expect(result,GiftClaimStatus.Claimed);
                GiftEligibilityTests.Check(result.Items.Select(item => (uint)item.m_templateID).SequenceEqual(catalog.Kits[school].Items),"persisted school-specific kit " + school);
            }
            Console.WriteLine("PASS: Raven atomic claims, capacity boundaries, ownership, retries, concurrent claims, rollback, exact IDs and equipment preservation");
        } finally {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database,hardDelete:true));
        }
    }
}
