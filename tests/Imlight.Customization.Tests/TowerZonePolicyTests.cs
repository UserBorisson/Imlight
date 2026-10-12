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
        var exitRun = TowerRunState.Create("exit-policy", owner, 123);
        foreach (var (room, exitTrigger) in new[] {
            (2, "Teleport location (WC_Golem_2 WC_Golem_1 Exit)"),
            (3, "Teleport location (WC_Golem_3 WC_Golem_2 Exit)"),
            (4, "Teleport location (WC_Golem_4 WC_Golem_3 Exit)"),
        }) {
            var resolvedExit = TowerZonePolicy.ResolveRoomResult(TowerZonePaths.GetRoomPath(room), exitRun,
                exitTrigger, new ResTeleport {
                m_destinationZone = TowerZonePaths.GetRoomPath(1),
                m_destinationLoc = "legacy-room-destination",
            }) as ResTeleport;
            Check(resolvedExit?.m_destinationZone == TowerZonePaths.EntryZone
                && resolvedExit.m_destinationLoc == "-518.1148,1207.106,36.78398,-0.4086321",
                "each physical exit trigger routes to the public Court");
        }
        var stairs = new[] {
            (1, "Teleport location (WC_Golem_2 WC_Golem_3 Entrance)"),
            (2, "Teleport location (WC_Golem_2 WC_Golem_3 Entrance)"),
            (3, "Teleport location (WC_Golem_3 WC_Golem_4 Entrance)"),
            (4, "Teleport location (WC_Golem_4 to WC_Golem_5)"),
            (6, "Teleport location (WC_Golem_2 WC_Golem_3 Entrance)"),
            (7, "Teleport location (WC_Golem_2 WC_Golem_3 Entrance)"),
            (8, "Teleport location (WC_Golem_3 WC_Golem_4 Entrance)"),
            (9, "Teleport location (WC_Golem_4 to WC_Golem_5)"),
        };
        foreach (var (floor, stairsTrigger) in stairs) {
            var currentRun = TowerRunState.Create($"stairs-floor-{floor}", owner, (ulong)floor);
            currentRun.CurrentFloor = floor;
            var currentRoom = TowerZonePaths.GetRoomPath(floor);
            var stairsTransfer = new ResTeleport { m_destinationZone = TowerZonePaths.GetRoomPath(floor + 1) };
            Check(TowerZonePolicy.ResolveRoomResult(currentRoom, currentRun, stairsTrigger, stairsTransfer) is null,
                $"floor {floor} stairs remain blocked before victory");
            currentRun.CurrentFloor++;
            Check(TowerZonePolicy.ResolveRoomResult(currentRoom, currentRun, stairsTrigger, stairsTransfer) is not null,
                $"floor {floor} stairs are authorized after victory");
        }
        var roomFiveExit = TowerZonePolicy.ResolveRoomResult(TowerZonePaths.GetRoomPath(5), exitRun,
            "Teleport location (WC_Golem_5 WC_Golem_4)", new ResTeleport {
                m_destinationZone = TowerZonePaths.EntryZone,
            });
        Check(roomFiveExit is ResTeleport, "floor 5 keeps its Court exit for checkpointed re-entry at floor 6");
        Check(!TowerZonePolicy.IsAllowedTowerRoomResult(new ResSpawn()), "legacy spawn trigger suppressed");
        Check(TowerZonePolicy.IsAllowedTowerRoomResult(new ResInitiateCombat()),
            "native duel initiation remains available for manifest combatants");

        PlannerRejectsRosterWhenNoSeparatedPlacementExists();
        PlannerKeepsEveryManifestEnemySeparatedAndPathAligned();

        Console.WriteLine("PASS: Tower transfer owner policy, resolved client destination, strict zone and roster validation");
    }

    private static void PlannerKeepsEveryManifestEnemySeparatedAndPathAligned() {
        var plannedEntries = BuildPlan("placement-adversarial-17", 2,
            (42u, 77UL, 1UL, new Vector3(0, 0, 0)),
            (43u, 78UL, 2UL, new Vector3(-7.8165f, 3.039f, 0)),
            (99u, 88UL, 3UL, new Vector3(200, 200, 0)));

        Check(plannedEntries.Length == 2, "planner creates exactly the two-enemy manifest roster");
        var plannedInfos = plannedEntries.Select(entry => (SpawnObjectInfo)PublicProperty(entry, "Info")).ToArray();
        Check(plannedInfos.All(info => info.m_templateID.Full is 42 or 43),
            "legacy unrelated spawn groups cannot enter the manifest roster");
        var plannedNodes = plannedEntries.Select(entry => (NodeObject)PublicProperty(entry, "Node")).ToArray();
        Check(plannedNodes.All(node => node.m_id.Full is 1 or 2),
            "manifest enemies are assigned nodes from their authored path");

        for (var first = 0; first < plannedInfos.Length; first++) {
            for (var second = first + 1; second < plannedInfos.Length; second++) {
                var dx = plannedInfos[first].m_location.X - plannedInfos[second].m_location.X;
                var dy = plannedInfos[first].m_location.Y - plannedInfos[second].m_location.Y;
                Check(MathF.Sqrt(dx * dx + dy * dy) >= 10f,
                    "every generated enemy pair is at least 10 horizontal units apart");
            }
        }

        foreach (var (entry, index) in plannedEntries.Select((entry, index) => (entry, index))) {
            var startNode = plannedNodes[index];
            var translatedPath = ((IEnumerable)PublicProperty(entry, "Nodes")).Cast<NodeObject>().ToArray();
            Check(translatedPath.Any(node => node.m_id.Full == startNode.m_id.Full
                && node.m_location == plannedInfos[index].m_location),
                "each translated path starts at its final generated spawn location");
        }
    }

    private static void PlannerRejectsRosterWhenNoSeparatedPlacementExists() {
        var plan = BuildPlan("placement-no-safe-pair", 2,
            (42u, 77UL, 1UL, new Vector3(0, 0, 0)));
        Check(plan.Length == 0, "planner rejects the roster when the only authored anchor cannot separate both enemies");
    }

    private static object[] BuildPlan(string encounterId, int enemyCount,
        params (uint TemplateId, ulong PathId, ulong NodeId, Vector3 Location)[] authoredCandidates) {
        var spawnData = new SpawnManager {
            m_spawners = [new SpawnObject {
                m_spawnList = authoredCandidates.Select(candidate => new SpawnItem {
                    m_percentChance = 100,
                    m_objectInfo = new SpawnObjectInfo {
                        m_templateID = candidate.TemplateId,
                        m_pathID = new GID(candidate.PathId),
                        m_kStartNodeType = StartNodeType.SNT_FIRST,
                    },
                }).ToList(),
            }],
        };
        var pathData = new PathTemplateList {
            m_pathList = authoredCandidates.Select(candidate => new PathObjectTemplate {
                m_id = new GID(candidate.PathId),
                m_nodeIDs = [new GID(candidate.NodeId)],
            }).ToList(),
        };
        var nodeData = new NodeTemplateList {
            m_nodeList = authoredCandidates.Select(candidate => new NodeObject {
                m_id = new GID(candidate.NodeId),
                m_location = candidate.Location,
            }).ToList(),
        };
        var planner = typeof(TowerZonePolicy).Assembly.GetType("Imlight.CoreLib.Game.Tower.TowerEnemySpawnPlanner")
            ?? throw new InvalidOperationException("Missing TowerEnemySpawnPlanner");
        var buildPlan = planner.GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing TowerEnemySpawnPlanner.Build");
        var result = (IEnumerable)(buildPlan.Invoke(null, [spawnData, pathData, nodeData, encounterId, enemyCount,
            new Func<GID, bool>(templateId => templateId.Full is 42 or 43)])
            ?? throw new InvalidOperationException("Tower spawn planner returned no result"));
        return result.Cast<object>().ToArray();
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static object PublicProperty(object value, string propertyName)
        => value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new InvalidOperationException($"Missing {value.GetType().Name}.{propertyName}");
}
