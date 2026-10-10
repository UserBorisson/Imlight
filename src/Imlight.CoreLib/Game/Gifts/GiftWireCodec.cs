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
 * NATIVE GIFT CODEC
 * ========================================================================
 * 
 * PURPOSE:
 * Encodes native gift lists and confirmation descriptions using generated client types.
 * 
 * USAGE EXAMPLE:
 * GiftWireCodec encodes gift listings for the native notification interface.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nikola Borissov with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Gifts;

public sealed record GiftListing(string GiftId, ulong RewardId, ulong RewardModifier = 0);
public sealed record GiftReward(string Description, string Type);

public static class GiftWireCodec {
    public static ByteString EncodeList(IReadOnlyList<GiftListing> gifts) {
        ArgumentNullException.ThrowIfNull(gifts);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var list = new PromoGiftList { m_promoGifts = [] };
        foreach (var gift in gifts) {
            if (gift is null || string.IsNullOrWhiteSpace(gift.GiftId) || gift.GiftId.Length > 128
                || gift.GiftId.Contains('\0') || gift.RewardId == 0 || !ids.Add(gift.GiftId)) {
                throw new ArgumentException("Gifts require unique bounded IDs and a nonzero reward reference.", nameof(gifts));
            }
            list.m_promoGifts.Add(new PromoGift {
                m_giftID = gift.GiftId,
                m_rewardID = gift.RewardId,
                m_rewardModifier = gift.RewardModifier,
                m_autoRedeem = false,
            });
        }
        return Encode(list);
    }

    public static ByteString EncodeRedemption(IReadOnlyList<GiftReward> rewards) {
        ArgumentNullException.ThrowIfNull(rewards);
        var list = new PromoRewardList { m_promoRewards = [] };
        foreach (var reward in rewards) {
            if (reward is null || string.IsNullOrWhiteSpace(reward.Description) || string.IsNullOrWhiteSpace(reward.Type)) {
                throw new ArgumentException("Rewards require a description and native reward type.", nameof(rewards));
            }
            list.m_promoRewards.Add(new PromoReward { m_desc = reward.Description, m_type = reward.Type });
        }
        return Encode(list);
    }

    private static ByteString Encode(PropertyClass value) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(value, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
            throw new InvalidOperationException("Native gift serialization failed.");
        }
        return data;
    }
}
