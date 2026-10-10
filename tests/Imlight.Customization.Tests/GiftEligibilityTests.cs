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
using Imlight.CoreLib.Shared.Behaviors;
internal static class GiftEligibilityTests {
    public static void Run() {
        var catalog = ContentCatalog.Load(Path.GetFullPath("content"));
        var campaign = new SchoolGiftCampaign(catalog);
        foreach (var school in Enum.GetValues<School>()) {
            var gift = campaign.GetAvailableGift(9001,Enum.Parse<MagicSchool>(school.ToString()));
            Check(gift is not null && gift.RewardId == catalog.Kits[school].BundleTemplateId,"school kit " + school);
            Check(gift.GiftId == campaign.GiftId(9001),"wizard-bound gift");
        }
        Check(campaign.GiftId(1) != campaign.GiftId(2),"two independent wizards");
        Check(campaign.GetAvailableGift(1,MagicSchool.None) is null,"invalid school");
        var disabled = new SchoolGiftCampaign(catalog with { Campaign = catalog.Campaign with { Enabled = false } });
        Check(disabled.GetAvailableGift(1,MagicSchool.Ice) is null,"disabled campaign");
        Check(campaign.ReceiptId(1) == new SchoolGiftCampaign(catalog with { ContentVersion = "next" }).ReceiptId(1),"version independent receipt");
        Check(campaign.TutorialItems.SequenceEqual(new ulong[] { 87256,126983 }),"tutorial needs basic wand and deck without QA items or gift");
        Console.WriteLine("PASS: all seven schools, independent wizards, disabled campaigns, stable receipts");
    }
    internal static void Check(bool value,string message) { if (!value) { throw new Exception(message); } }
}
