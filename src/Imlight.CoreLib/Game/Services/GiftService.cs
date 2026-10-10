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

using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;

namespace Imlight.CoreLib.Game.Services;

internal sealed class GiftService(SessionActor sessionActor) : MessageService(sessionActor) {
    private SchoolGiftCampaign Campaign => SchoolGiftCampaign.Configured;
    private SchoolGiftClaimCollection Claims => new(PlayerDatabase.Instance.Store,Campaign,
        ConfigurationManager.GetValue("Character.MaxInventoryItems",75),
        id => (WizClientObjectItem)CoreObjectFactory.FinalizeCoreObject(id));

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new GiftService(parentActor));

    private GiftListing Available() {
        var wizard = GetActiveWizard();
        if (Campaign is null || wizard is null || GetActiveAccount()?.AccountId != wizard.AccountId) { return null; }
        return Claims.HasClaim(wizard.CharId) ? null : Campaign.GetAvailableGift(wizard);
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void Notify() {
        if (Campaign is null) { return; }
        SendToSocket(new GAME_5_PROTOCOL.MSG_NOTIFY_GIFT { Gift = Available() is null ? 0 : 1 });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUEST_GIFTS))]
    private void ListGifts(GAME_5_PROTOCOL.MSG_REQUEST_GIFTS request) {
        if (Campaign is null) { return; }
        var gift = Available();
        SendToSocket(new GAME_5_PROTOCOL.MSG_RECEIVE_GIFTS {
            Success = 1, Data = GiftWireCodec.EncodeList(gift is null ? [] : [gift]), AutoRedeem = 0,
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REDEEM_GIFT))]
    private void Redeem(GAME_5_PROTOCOL.MSG_REDEEM_GIFT request) {
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();
        if (Campaign is null || wizard is null || account is null || account.AccountId != wizard.AccountId) { return; }
        var gift = Campaign.GetAvailableGift(wizard);
        if (gift is null) {
            SendToSocket(GiftResponses.Redemption(new GiftListing(request.GiftID.ToString(),1),new(GiftClaimStatus.NotEligible,[],[])));
            return;
        }
        var result = WizardCollection.WithWriteLane(wizard.CharId,() => {
            var committed = Claims.TryClaim(account.AccountId,wizard.CharId,request.GiftID.ToString());
            if (committed.Status == GiftClaimStatus.Claimed) {
                foreach (var item in committed.Items) {
                    wizard.InventoryBehavior.InventoryItemIds.Add(item.m_globalID);
                    wizard.InventoryBehavior.Items.Add(item);
                }
            }
            return committed;
        });
        if (result.Status == GiftClaimStatus.Claimed) {
            var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
            foreach (var item in result.Items) {
                if (serializer.Serialize(item,PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit,out var data)) {
                    SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.CharId, SerializedItem = data });
                }
            }
        }
        if (result.Status == GiftClaimStatus.RetryableFailure) {
            Logger.Warning("School gift claim could not commit for wizard {0}.",Logger.Args(wizard.CharId));
        }
        SendToSocket(GiftResponses.Redemption(gift,result));
        Notify();
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_DELETE_GIFT))]
    private void Delete(GAME_5_PROTOCOL.MSG_DELETE_GIFT request) {
        Notify();
    }
}
