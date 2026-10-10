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
 * DISPOSABLE NATIVE GIFT PROBE
 * ========================================================================
 * 
 * PURPOSE:
 * Exercises native manual gift listing and eight-item redemption in development only.
 * 
 * USAGE EXAMPLE:
 * Compile with EnableGiftProbe=true in the isolated development environment.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nikola Borissov with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

internal sealed class GiftProbeService(SessionActor sessionActor) : MessageService(sessionActor) {
    private const string ProbeId = "local-native-gift-probe-v1";
    private const ulong ProbeTemplate = 87256;
    private bool _claimed;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new GiftProbeService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void Notify() {
        if (GetActiveAccount()?.Username == "giftprobe" && GetActiveWizard() is not null) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_NOTIFY_GIFT { Gift = _claimed ? 0 : 1 });
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUEST_GIFTS))]
    private void ListGifts(GAME_5_PROTOCOL.MSG_REQUEST_GIFTS request) {
        if (GetActiveAccount()?.Username != "giftprobe" || GetActiveWizard() is null) { return; }
        SendToSocket(new GAME_5_PROTOCOL.MSG_RECEIVE_GIFTS {
            Success = 1,
            Data = GiftWireCodec.EncodeList(_claimed ? [] : [new GiftListing(ProbeId, ProbeTemplate)]),
            AutoRedeem = 0,
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REDEEM_GIFT))]
    private void Redeem(GAME_5_PROTOCOL.MSG_REDEEM_GIFT request) {
        var wizard = GetActiveWizard();
        if (GetActiveAccount()?.Username != "giftprobe" || wizard is null || _claimed || request.GiftID.ToString() != ProbeId) { return; }
        var items = wizard.GrantStarterItems(Enumerable.Repeat(ProbeTemplate, 8));
        _claimed = true;
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        foreach (var item in items) {
            if (serializer.Serialize(item, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
                SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.CharId, SerializedItem = data });
            }
        }
        SendToSocket(new GAME_5_PROTOCOL.MSG_GIFT_REDEEMED {
            Success = 1, GiftID = ProbeId, GID = items[0].m_globalID, TemplateID = ProbeTemplate, Quantity = 8,
            Data = GiftWireCodec.EncodeRedemption(Enumerable.Range(1, 8).Select(index => new GiftReward("Local probe wand " + index, "Weapon")).ToArray()),
            AutoRedeem = 0,
        });
        SendToSocket(new GAME_5_PROTOCOL.MSG_NOTIFY_GIFT { Gift = 0 });
    }
}
