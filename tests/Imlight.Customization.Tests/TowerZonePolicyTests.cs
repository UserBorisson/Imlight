using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Packets;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using System.Collections;
using System.Reflection;

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

        Check(!TowerZonePolicy.IsAllowedTowerRoomResult(new ResTeleport {
            m_destinationZone = TowerZonePaths.GetRoomPath(2)
        }), "legacy Tower room progression teleport suppressed");
        Check(TowerZonePolicy.IsAllowedTowerRoomResult(new ResTeleport {
            m_destinationZone = TowerZonePaths.EntryZone
        }), "Court exit preserved");
        Check(!TowerZonePolicy.IsAllowedTowerRoomResult(new ResSpawn()), "legacy spawn trigger suppressed");
        Check(TowerZonePolicy.IsAllowedTowerRoomResult(new ResInitiateCombat()),
            "native duel initiation remains available for manifest combatants");

        var authoredInfo = new SpawnObjectInfo {
            m_templateID = 42,
            m_pathID = new GID(77),
            m_kStartNodeType = StartNodeType.SNT_RANDOM,
        };
        var unrelatedInfo = new SpawnObjectInfo {
            m_templateID = 99,
            m_pathID = new GID(88),
            m_kStartNodeType = StartNodeType.SNT_FIRST,
        };
        var spawnData = new SpawnManager {
            m_spawners = [new SpawnObject {
                m_spawnList = [
                    new SpawnItem { m_percentChance = 100, m_objectInfo = authoredInfo },
                    new SpawnItem { m_percentChance = 100, m_objectInfo = unrelatedInfo },
                ],
            }],
        };
        var pathData = new PathTemplateList {
            m_pathList = [new PathObjectTemplate { m_id = new GID(77), m_nodeIDs = [new GID(1), new GID(2)] }],
        };
        var nodeData = new NodeTemplateList {
            m_nodeList = [
                new NodeObject { m_id = new GID(1), m_location = new Vector3(0, -45, 0), m_direction = 0 },
                new NodeObject { m_id = new GID(2), m_location = new Vector3(4, -45, 0), m_direction = 90 },
            ],
        };
        var planner = typeof(TowerZonePolicy).Assembly.GetType("Imlight.CoreLib.Game.Tower.TowerEnemySpawnPlanner")
            ?? throw new InvalidOperationException("Missing TowerEnemySpawnPlanner");
        var buildPlan = planner.GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing TowerEnemySpawnPlanner.Build");
        var spawnPlan = (IEnumerable)(buildPlan.Invoke(null, [
            spawnData, pathData, nodeData, manifest.EncounterId, 3, new Func<GID, bool>(templateId => templateId.Full == 42)
        ]) ?? throw new InvalidOperationException("Tower spawn planner returned no result"));
        var plannedEntries = spawnPlan.Cast<object>().ToArray();
        Check(plannedEntries.Length == 3, "room spawn data supplies exactly the authoritative manifest roster size");
        var plannedInfos = plannedEntries.Select(entry => (SpawnObjectInfo)PublicProperty(entry, "Info")).ToArray();
        Check(plannedInfos.All(info => info.m_templateID == 42),
            "legacy unrelated spawn groups cannot enter the manifest roster");
        var plannedNodes = plannedEntries.Select(entry => (NodeObject)PublicProperty(entry, "Node")).ToArray();
        Check(plannedNodes.All(node => node.m_id.Full is 1 or 2),
            "manifest enemies are assigned nodes from their authored path");
        var plannedLocations = plannedInfos.Select(info => info.m_location).ToArray();
        Check(plannedLocations.Distinct().Count() == plannedLocations.Length,
            "multiple manifest enemies receive separated spawn positions");

        Console.WriteLine("PASS: Tower transfer owner policy, resolved client destination, strict zone and roster validation");
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static object PublicProperty(object value, string propertyName)
        => value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new InvalidOperationException($"Missing {value.GetType().Name}.{propertyName}");
}
