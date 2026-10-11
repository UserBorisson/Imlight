using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Packets;

internal static class TowerZonePolicyTests {
    public static void Run() {
        const ulong owner = 402UL;
        for (var floor = 1; floor <= 5; floor++) {
            var zone = TowerZonePaths.GetRoomPath(floor);
            Check(TowerZonePaths.IsRoomPath(zone), "each exact physical room is recognized");
            var original = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = zone, DestinationLocation = "Start",
                OwnerCharId = 0, IsPrivate = false, ResetInstance = true, SendToClient = true
            };
            Check(TowerTransferPolicy.IsEnabledTowerRoomRequest(original, true), "non-private enabled room is Tower");
            Check(TowerTransferPolicy.TryBindAuthenticatedOwner(original, owner, out var bound), "authenticated entry");
            Check(bound.IsPrivate && bound.OwnerCharId == owner && bound.ResetInstance,
                "owner and private instance enforced");
            Check(!original.IsPrivate && original.OwnerCharId == 0, "input transfer is never mutated");
            Check(!TowerTransferPolicy.IsEnabledTowerRoomRequest(original, false), "disabled mode is native");
            Check(!TowerTransferPolicy.TryBindAuthenticatedOwner(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = zone, OwnerCharId = owner + 1
            }, owner, out _), "spoofed owner rejected");
            Check(!TowerTransferPolicy.TryBindAuthenticatedOwner(original, 0, out _), "missing authenticated owner rejected");
        }

        foreach (var invalid in new [] {
            TowerZonePaths.EntryZone, TowerZonePaths.GetRoomPath(1) + "_suffix",
            TowerZonePaths.GetRoomPath(5) + "1", "WizardCity/UnrelatedRoom", ""
        }) {
            Check(!TowerZonePaths.IsRoomPath(invalid), "non-exact or court route excluded");
        }

        var input = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = TowerZonePaths.GetRoomPath(1), DestinationLocation = "Start", OwnerCharId = owner
        };
        var response = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
            ResolvedDestinationZone = TowerZonePaths.GetRoomPath(3),
            ResolvedDestinationLocation = "Start"
        };
        var resolved = TowerTransferPolicy.ApplyResolvedDestination(input, response);
        Check(resolved.DestinationZone == TowerZonePaths.GetRoomPath(3) && input.DestinationZone != resolved.DestinationZone,
            "client-bound room is resolved without mutating request");
        Check(!TowerTransferPolicy.IsTowerExtractionRequest(true, TowerZonePaths.GetRoomPath(1),
            TowerZonePaths.GetRoomPath(2)), "inter-room transfer is not extraction");
        Check(TowerTransferPolicy.IsTowerExtractionRequest(true, TowerZonePaths.GetRoomPath(1),
            TowerZonePaths.EntryZone), "court is a valid extraction target");
        Check(!TowerTransferPolicy.IsTowerExtractionRequest(false, TowerZonePaths.GetRoomPath(1),
            TowerZonePaths.EntryZone), "disabled-mode exit remains native");

        var state = TowerRunState.Create("policy", owner, 123);
        var manifest = state.BuildCurrentManifest();
        Check(TowerZonePolicy.IsValidManifest(state, TowerZonePaths.GetRoomPath(1)), "valid manifest accepted");
        Check(!TowerZonePolicy.IsValidManifest(state, TowerZonePaths.GetRoomPath(2)), "wrong room rejected");
        manifest.EncounterId = "";
        Check(!TowerZonePolicy.IsValidManifest(state, TowerZonePaths.GetRoomPath(1)), "missing encounter identity rejected");
        manifest.EncounterId = "policy/attempt-1/floor-1/recovery-0";
        Check(!TowerZonePolicy.IsCompleteManifestRoster(2, [0], [0,1]), "missing profile rejected");
        Check(!TowerZonePolicy.IsCompleteManifestRoster(2, [0,1], [0,0]), "duplicate actor slot rejected");
        Check(TowerZonePolicy.IsCompleteManifestRoster(2, [1,0], [0,1]), "complete roster accepted");

        Console.WriteLine("PASS: Tower transfer owner policy, resolved client destination, strict zone and roster validation");
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
