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

using Imlight.CoreLib.Game.Gifts;
internal static class GiftServiceTests {
    public static void Run() {
        var gift = new GiftListing("school-starter-v1/42",9000102);
        foreach(var status in new[] { GiftClaimStatus.Claimed, GiftClaimStatus.AlreadyClaimed }) {
            var response = GiftResponses.Redemption(gift,new(status,[1,2,3,4,5,6,7,8],[]));
            GiftEligibilityTests.Check(response.Success == 1 && response.GID == 0 && response.Quantity == 1 && response.TemplateID == 9000102 && response.AutoRedeem == 0,"manual bundle confirmation " + status);
        }
        var full = GiftResponses.Redemption(gift,new(GiftClaimStatus.InventoryFull,[],[]));
        GiftEligibilityTests.Check(full.Success == 0 && full.ErrorCode == 5 && full.AutoRedeem == 0,"native inventory-full failure");
        foreach(var status in new[] { GiftClaimStatus.InvalidGift, GiftClaimStatus.NotEligible, GiftClaimStatus.RetryableFailure }) {
            GiftEligibilityTests.Check(GiftResponses.Redemption(gift,new(status,[],[])).Success == 0,"failure never acknowledges success");
        }
        Console.WriteLine("PASS: manual native bundle confirmation, retry acknowledgements and failure responses");
    }
}
