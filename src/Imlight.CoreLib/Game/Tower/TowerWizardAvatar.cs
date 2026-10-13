using System;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.ObjectProperty.Bit;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Native player body and public identity for an ephemeral, AI controlled arena wizard.</summary>
internal static class TowerWizardAvatar {
    private static readonly string[] Names = [
        "Rowan Emberward", "Eliza Frostweaver", "Dorian Stormcaller", "Nora Mythwarden",
        "Jasper Nightbloom", "Iris Willowheart", "Adrian Sandwalker", "Clara Dawnshield",
        "Felix Ashthorn", "Sylvia Moonbrook", "Tristan Runekeeper", "Hazel Starfall", "Gavin Lightward"
    ];

    internal static void Configure(WizClientObject obj, TowerEnemySpawn profile, long floor) {
        obj.m_templateID = 1;
        obj.m_characterId = obj.m_globalID;
        obj.m_fScale = 1f;
        var identity = (int)((Math.Max(1, floor) - 1) % Names.Length);
        var identityTemplate = profile.TemplateId == 0 ? null
            : CoreObjectFactory.GetCoreTemplate(profile.TemplateId) as GameObjectTemplate;
        var nativeName = Locale.GetEnglishName(identityTemplate?.m_displayName?.ToString() ?? string.Empty);
        var nativeIdentity = identityTemplate?.m_objectName?.ToString() ?? string.Empty;
        var avatar = obj.m_inactiveBehaviors.OfType<WizardCharacterBehavior>().Single();
        avatar.m_eRace = eRace.Human;
        avatar.m_eGender = nativeIdentity.Contains("Female", StringComparison.Ordinal) ? eGender.Female
            : nativeIdentity.Contains("Male", StringComparison.Ordinal) ? eGender.Male
            : identity % 2 == 0 ? eGender.Male : eGender.Female;
        avatar.m_nHairModel = (Bui4)(identity % 8);
        avatar.m_nHairColor = (Bui7)(identity % 7);
        avatar.m_nSkinColor = (Bui4)(identity % 6);
        avatar.m_nSkinDecal = (Bui4)(identity % 6);
        // These are native creation wardrobe model/color indices, not creature render assets.
        avatar.m_nHatModel = (Bui2)(identity % 3);
        avatar.m_nTorsoModel = (Bui2)(identity % 3);
        avatar.m_nFeetModel = (Bui2)(identity % 3);
        avatar.m_nWandModel = (Bui2)0;
        var color = (byte)(profile.SchoolId switch {
            "Fire" => 0, "Ice" => 1, "Storm" => 4, "Myth" => 3,
            "Life" => 2, "Death" => 6, _ => 5
        });
        avatar.m_nHatColor = (Bui5)color;
        avatar.m_nTorsoColor = (Bui5)color;
        avatar.m_nFeetColor = (Bui5)color;
        var name = obj.m_inactiveBehaviors.OfType<ClientWizPlayerNameBehavior>().Single();
        // The existing native locale key is also used by the Tome. Reuse that curated name
        // on the player body so floor selection and the visible opponent agree.
        var displayName = string.IsNullOrWhiteSpace(nativeName) ? Names[identity] : nativeName;
        // Keep the native wizard body/choreography and use an explicit two-line opponent label.
        // SchoolIcon tokens come from the installed MadlibIcons.xml.
        name.m_wsNameOverride = $"{displayName}\n<image;{profile.SchoolId}SchoolIcon> Level {Math.Max(1, floor)}";
        name.m_eGender = avatar.m_eGender;
        name.m_eRace = eRace.Human;
        name.m_useRank = false;
        var school = obj.m_inactiveBehaviors.OfType<ClientMagicSchoolBehavior>().Single();
        school.m_schoolOfFocus = StringHash.Compute(profile.SchoolId);
        // Public nameplate progression is independent of the capped native combat level.
        school.m_level = (int)Math.Clamp(floor, 1L, int.MaxValue);
    }
}
