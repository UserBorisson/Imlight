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

using System;
using System.Linq;
using System.Collections.Generic;
using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Newtonsoft.Json.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using SchoolContentCompiler;
namespace Imlight.CoreLib.WizardData.Collections;

public sealed class SchoolGiftClaimCollection(IDocumentStore store, SchoolGiftCampaign campaign, int capacity, Func<uint,WizClientObjectItem> createItem) {
    private sealed class DocumentReference { public string DocumentId { get; set; } }

    public bool HasClaim(ulong wizardId) {
        using var session = store.OpenSession();
        return session.Advanced.Exists(campaign.ReceiptId(wizardId));
    }

    public GiftClaimResult TryClaim(ulong accountId, ulong wizardId, string giftId) {
        if (giftId != campaign.GiftId(wizardId)) { return Result(GiftClaimStatus.InvalidGift); }
        for (var attempt = 0; attempt < 3; attempt++) {
            try {
                using var session = store.OpenSession();
                session.Advanced.UseOptimisticConcurrency = true;
                var reference = session.Advanced.RawQuery<DocumentReference>("from Wizards where CharId = $wizardId select id() as DocumentId")
                    .AddParameter("wizardId",wizardId).WaitForNonStaleResults(TimeSpan.FromSeconds(5)).SingleOrDefault();
                if (reference is null) { return Result(GiftClaimStatus.NotEligible); }
                var wizard = session.Load<JObject>(reference.DocumentId);
                if (wizard is null || wizard["AccountId"]?.Value<ulong>() != accountId) { return Result(GiftClaimStatus.NotEligible); }
                if (!Enum.TryParse<MagicSchool>(wizard["MagicSchoolBehavior"]?["MagicSchool"]?.ToString(),out var school)) {
                    return Result(GiftClaimStatus.NotEligible);
                }
                var gift = campaign.GetAvailableGift(wizardId,school);
                if (gift is null) { return Result(GiftClaimStatus.NotEligible); }
                var receiptId = campaign.ReceiptId(wizardId);
                var existing = session.Load<SchoolGiftClaim>(receiptId);
                if (existing is not null) { return new(GiftClaimStatus.AlreadyClaimed,existing.ItemIds,[]); }
                if (wizard["InventoryBehavior"]?["InventoryItemIds"] is not JArray inventory) { return Result(GiftClaimStatus.NotEligible); }
                var kit = campaign.Catalog.Kits[Enum.Parse<School>(school.ToString())];
                var occupied = inventory.Select(id=>id.Value<ulong>()).Concat(
                    (wizard["EquipmentBehavior"]?["EquippedItemIds"] as JArray ?? []).Select(id=>id.Value<ulong>())).Distinct().Count();
                if (capacity - occupied < kit.Items.Length) { return Result(GiftClaimStatus.InventoryFull); }
                var items = kit.Items.Select(createItem).ToArray();
                if (items.Any(item => item is null || item.m_globalID == 0) || items.Select(item=>item.m_globalID).Distinct().Count() != items.Length) {
                    return Result(GiftClaimStatus.RetryableFailure);
                }
                var itemIds = new List<ulong>();
                foreach (var item in items) {
                    item.m_characterId = wizardId;
                    var itemId = (ulong)item.m_globalID;
                    itemIds.Add(itemId);
                    inventory.Add(new JValue(itemId));
                    session.Store(item,string.Empty,$"school-gift-items/{campaign.Catalog.Campaign.Id}/{wizardId}/{itemId}");
                    session.Advanced.GetMetadataFor(item)["@collection"] = "WizardItems";
                }
                var receipt = new SchoolGiftClaim {
                    CampaignId = campaign.Catalog.Campaign.Id, ContentVersion = campaign.Catalog.ContentVersion,
                    AccountId = accountId, WizardId = wizardId, ItemIds = itemIds, ClaimedUtc = DateTime.UtcNow,
                };
                session.Store(receipt,string.Empty,receiptId);
                session.SaveChanges();
                return new(GiftClaimStatus.Claimed,itemIds,items);
            } catch (ConcurrencyException) {
                if (attempt == 2) { return Result(GiftClaimStatus.RetryableFailure); }
            } catch (Exception) {
                return Result(GiftClaimStatus.RetryableFailure);
            }
        }
        return Result(GiftClaimStatus.RetryableFailure);
    }

    private static GiftClaimResult Result(GiftClaimStatus status) => new(status,[],[]);
}
