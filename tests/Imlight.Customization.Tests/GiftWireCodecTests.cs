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

using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Gifts;

internal static class GiftWireCodecTests {
    internal static void Run() {
        var gifts = new[] { new GiftListing("fixture<&>gift", 87256, 3), new GiftListing("fixture-second", 126983) };
        var encoded = GiftWireCodec.EncodeList(gifts);
        const string verifiedFixture = "2AEA7A2202000000298A5278D85401000000000003000000000000000E00666978747572653C263E6769667400298A527807F001000000000000000000000000000E00666978747572652D7365636F6E64";
        if (Convert.ToHexString((byte[])encoded) != verifiedFixture) { throw new Exception("Gift list no longer matches the pinned native payload fixture"); }
        var serializer = new ObjectSerializer(Versionable: false);
        if (!serializer.Deserialize((byte[])encoded, 0x18, out PromoGiftList list)) { throw new Exception("Gift list must deserialize with native nonversionable serializer"); }
        if (list.m_promoGifts.Count != 2 || list.m_promoGifts[0].m_giftID != gifts[0].GiftId
            || (ulong)list.m_promoGifts[0].m_rewardID != 87256 || (ulong)list.m_promoGifts[0].m_rewardModifier != 3
            || list.m_promoGifts.Any(gift => gift.m_autoRedeem)) { throw new Exception("Gift fields or manual claim flag failed round-trip"); }
        if (!((byte[])encoded).SequenceEqual((byte[])GiftWireCodec.EncodeList(gifts))) { throw new Exception("Gift encoding is not deterministic"); }
        var rewards = new[] { new GiftReward("Ice Starter Hat <test>", "Hat"), new GiftReward("Ice Starter Ring", "Ring") };
        if (!serializer.Deserialize((byte[])GiftWireCodec.EncodeRedemption(rewards), 0x18, out PromoRewardList redeemed)
            || redeemed.m_promoRewards.Count != 2 || redeemed.m_promoRewards[0].m_desc != rewards[0].Description
            || redeemed.m_promoRewards[0].m_type != "Hat") { throw new Exception("Redemption reward descriptions failed round-trip"); }
        ExpectInvalid(() => GiftWireCodec.EncodeList([new GiftListing("", 1)]));
        ExpectInvalid(() => GiftWireCodec.EncodeList([new GiftListing("valid", 0)]));
        ExpectInvalid(() => GiftWireCodec.EncodeList([new GiftListing("same", 1), new GiftListing("same", 2)]));
        Console.WriteLine("PASS: native gift codec round-trip, literal strings, manual flags, determinism and invalid input");
    }

    private static void ExpectInvalid(System.Action action) {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid gift input was accepted");
    }
}
