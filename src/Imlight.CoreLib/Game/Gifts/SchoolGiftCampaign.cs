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
using Imlight.Common;
using SchoolContentCompiler;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
namespace Imlight.CoreLib.Game.Gifts;
public sealed class SchoolGiftCampaign(ValidatedCatalog catalog) {
    private static readonly Lazy<SchoolGiftCampaign> s_configured = new(() => {
        var path = ConfigurationManager.GetValue("Customization.ContentPath",string.Empty);
        return string.IsNullOrWhiteSpace(path) ? null : new SchoolGiftCampaign(ContentCatalog.Load(path));
    });
    public static SchoolGiftCampaign Configured => s_configured.Value;
    public ValidatedCatalog Catalog { get; } = catalog;
    public ulong[] TutorialItems => [87256,126983];
    public string GiftId(ulong wizardId) => $"{Catalog.Campaign.Id}/{wizardId}";
    public string ReceiptId(ulong wizardId) => $"school-gift-claims/{GiftId(wizardId)}";
    public GiftListing GetAvailableGift(Wizard wizard) => GetAvailableGift(wizard.CharId, wizard.MagicSchoolBehavior.MagicSchool);
    public GiftListing GetAvailableGift(ulong wizardId, MagicSchool school) {
        if (!Catalog.Campaign.Enabled || wizardId == 0 || !Enum.TryParse<School>(school.ToString(),out var definedSchool)
            || !Catalog.Kits.TryGetValue(definedSchool,out var kit)) { return null; }
        return new GiftListing(GiftId(wizardId),kit.BundleTemplateId);
    }
}
