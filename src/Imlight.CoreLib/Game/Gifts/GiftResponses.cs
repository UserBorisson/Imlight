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
using Imcodec.MessageLayer.Generated;
namespace Imlight.CoreLib.Game.Gifts;
public static class GiftResponses {
    public static GAME_5_PROTOCOL.MSG_GIFT_REDEEMED Redemption(GiftListing gift, GiftClaimResult result) => new() {
        Success = result.Status is GiftClaimStatus.Claimed or GiftClaimStatus.AlreadyClaimed ? 1 : 0,
        GiftID = gift.GiftId, GID = 0, TemplateID = gift.RewardId, Quantity = 1, AutoRedeem = 0,
        Data = GiftWireCodec.EncodeRedemption([]),
        ErrorCode = result.Status == GiftClaimStatus.InventoryFull ? 5 : 0,
    };
}
