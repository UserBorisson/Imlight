using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Packets;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

internal static class TowerActorIntegrationTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        var database = "LocalTowerActorTests-" + Guid.NewGuid().ToString("N");
        using var store = new DocumentStore {
            Urls = ["http://127.0.0.1:8180"],
            Database = database,
            Conventions = { UseOptimisticConcurrency = true },
        }.Initialize();
        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));

        try {
            InitializeLogging();
            const ulong wizardId = 812347UL;
            var collectionType = RequireType("Imlight.CoreLib.Game.Tower.TowerRunCollection");
            var collection = Activator.CreateInstance(collectionType, store)
                ?? throw new InvalidOperationException("Could not create TowerRunCollection");
            var run = Call(collection, "CreateRun", "actor-route-run", wizardId, 0xCAFEBABEUL);

            var containerType = RequireType("Imlight.CoreLib.Game.World.InstanceContainer");
            var shouldAcceptRun = containerType.GetMethod("ShouldAcceptTowerRunState", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing InstanceContainer.ShouldAcceptTowerRunState");
            var terminalRun = TowerRunState.Create("completed-run", wizardId, 0xA11CEUL);
            terminalRun.Status = TowerRunStatus.Completed;
            terminalRun.CreatedUtc = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
            var delayedPriorRun = TowerRunState.Create("prior-run", wizardId, 0xBEEFUL);
            delayedPriorRun.CreatedUtc = terminalRun.CreatedUtc.AddSeconds(-1);
            Check(!(bool)shouldAcceptRun.Invoke(null, [terminalRun, delayedPriorRun])!,
                "a delayed snapshot from an older run cannot replace a terminal run");
            var laterRun = TowerRunState.Create("later-run", wizardId, 0xC0FFEEUL);
            laterRun.CreatedUtc = terminalRun.CreatedUtc.AddSeconds(1);
            Check((bool)shouldAcceptRun.Invoke(null, [terminalRun, laterRun])!,
                "a newer run can replace a terminal run");

            var propsFactory = containerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method => method.Name == "Props" && method.GetParameters().Length == 3);
            TestLoadedZoneActor.Reset();
            Func<string, uint, TowerRunState, Props> zonePropsFactory = (_, _, _) => Props.Create(() => new TestLoadedZoneActor());
            var props = propsFactory.Invoke(null, [wizardId, collection, zonePropsFactory]) as Props
                ?? throw new InvalidOperationException("Could not create tower instance container props");

            using var system = ActorSystem.Create("TowerActorIntegrationTests");
            var container = system.ActorOf(props, "tower-instance");
            var queryType = RequireType("Imlight.CoreLib.Game.Tower.TowerInstanceRouteQuery");
            var query = Activator.CreateInstance(queryType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not create tower route query");
            Set(query, "ActivateRun", run);
            Set(query, "Transfer", NewTransfer(wizardId, 1));

            var firstRoute = AskRoute(container, query);
            Equal(TowerRoomPath(1), StringProperty(GetProperty(firstRoute, "Transfer"), "DestinationZone"),
                "first logical floor maps to physical room 1");
            Check(!(bool)GetProperty(firstRoute, "HasZone"), "first route requests room loading");
            Equal(TowerRoomPath(1), StringProperty(firstRoute, "ResolvedDestinationZone"),
                "initial route explicitly includes a client-facing destination");
            Equal("Start", StringProperty(firstRoute, "ResolvedDestinationLocation"),
                "resolved route retains the location");
            LoadZone(container, 1);
            Check((bool)GetProperty(AskRoute(container, query), "HasZone"), "loaded floor 1 room is reused");
            WaitUntil(() => TestLoadedZoneActor.Created == 1, "initial room actor was created");

            for (var floor = 1; floor <= 5; floor++) {
                var manifest = GetProperty(run, "CurrentManifest");
                var resolution = Activator.CreateInstance(RequireType("Imlight.CoreLib.Game.Tower.TowerEncounterResolution"))
                    ?? throw new InvalidOperationException("Could not create floor resolution");
                Set(resolution, "AuthorityEpoch", LongProperty(run, "AuthorityEpoch"));
                Set(resolution, "EncounterId", StringProperty(manifest, "EncounterId"));
                Set(resolution, "PlayerCount", 1);
                Set(resolution, "AlivePlayerCount", 1);
                Set(resolution, "AdmittedPlayerIds", new ulong[] { wizardId });
                var enemyCount = ((ICollection)GetProperty(manifest, "Enemies")).Count;
                var admittedSlots = Enumerable.Range(0, enemyCount).ToArray();
                Check((bool)Call(run, "TryBeginEncounter", new ulong[] { wizardId }, admittedSlots),
                    $"floor {floor} combat starts with its complete admitted roster");
                Set(resolution, "AdmittedEnemySlots", admittedSlots);
                Set(resolution, "CombatDeathEnemySlots", admittedSlots);
                Equal("Victory", Call(run, "ApplyResolution", resolution).ToString(), $"actor route floor {floor} clear");
                Check((bool)Call(collection, "TrySave", run, LongProperty(run, "AuthorityEpoch")), $"floor {floor} persists before route");

                var nextQuery = Activator.CreateInstance(queryType, nonPublic: true)
                    ?? throw new InvalidOperationException("Could not create next route query");
                var originalRequestedTransfer = NewTransfer(wizardId, floor);
                Set(nextQuery, "Transfer", originalRequestedTransfer);
                var nextRoute = AskRoute(container, nextQuery);
                Equal(TowerRoomPath(floor + 1), StringProperty(GetProperty(nextRoute, "Transfer"), "DestinationZone"),
                    $"logical floor {floor + 1} routes to its canonical room");
                Equal(TowerRoomPath(floor + 1), StringProperty(nextRoute, "ResolvedDestinationZone"),
                    "the resolved physical room is returned explicitly");
                Equal(TowerRoomPath(floor), originalRequestedTransfer.DestinationZone,
                    "a room remap never mutates the original actor transfer");
                if (floor == 5) {
                    Check(!(bool)GetProperty(nextRoute, "HasZone"), "floor 6 retires the stale floor 1 room before loading its new encounter");
                    WaitUntil(() => TestLoadedZoneActor.Stopped >= 1, "stale room actor reached PostStop");
                    LoadZone(container, 6);
                    Check((bool)GetProperty(AskRoute(container, nextQuery), "HasZone"), "room reload after encounter retirement completes");
                    WaitUntil(() => TestLoadedZoneActor.Created == 2, "room actor was recreated for floor 6");
                }
            }

            var acquiredRun = Call(collection, "AcquireAuthority", "actor-route-run", wizardId);
            var recoveredQuery = Activator.CreateInstance(queryType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not create recovered route query");
            Set(recoveredQuery, "ActivateRun", acquiredRun);
            Set(recoveredQuery, "Transfer", NewTransfer(wizardId, 6));
            Check(!(bool)GetProperty(AskRoute(container, recoveredQuery), "HasZone"),
                "authority reacquisition retires an already loaded room from the prior epoch");
            WaitUntil(() => TestLoadedZoneActor.Stopped >= 2, "prior epoch room actor reached PostStop");
            LoadZone(container, 6);
            Check((bool)GetProperty(AskRoute(container, recoveredQuery), "HasZone"), "room reload after authority reacquisition completes");
            WaitUntil(() => TestLoadedZoneActor.Created == 3, "room actor was recreated for the recovered epoch");

            var staleRunChanged = Activator.CreateInstance(RequireType("Imlight.CoreLib.Game.Tower.TowerRunChanged"), nonPublic: true)
                ?? throw new InvalidOperationException("Could not create stale run notification");
            Set(staleRunChanged, "State", run);
            var currentEpochQuery = Activator.CreateInstance(queryType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not create current epoch route query");
            Set(currentEpochQuery, "Transfer", NewTransfer(wizardId, 6));
            var currentEpochRoute = AskOrderedRoute(system, container, staleRunChanged, currentEpochQuery);
            Check((bool)GetProperty(currentEpochRoute, "HasZone"), "a delayed lower-epoch run notification cannot evict the recovered room");
            Equal(3, TestLoadedZoneActor.Created, "stale notification does not create another room actor");
            Equal(2, TestLoadedZoneActor.Stopped, "stale notification does not retire the recovered room actor");

            TestLoadedZoneActor.StopLatest();
            WaitUntil(() => TestLoadedZoneActor.Stopped >= 3, "failed room actor stopped");
            Check(!(bool)GetProperty(AskRoute(container, currentEpochQuery), "HasZone"),
                "a stopped room actor is evicted before the next transfer is routed");
            LoadZone(container, 6);
            WaitUntil(() => TestLoadedZoneActor.Created == 4, "room actor recreated after an unexpected stop");
            Check((bool)GetProperty(AskRoute(container, currentEpochQuery), "HasZone"),
                "a replacement room actor is available after recovery");

            var worldType = RequireType("Imlight.CoreLib.Game.World.GameWorld");
            var ownerSelector = worldType.GetMethod("SelectInstancedLoadOwners", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing GameWorld.SelectInstancedLoadOwners");
            var owners = (ulong[])ownerSelector.Invoke(null, [
                TowerRoomPath(1),
                wizardId,
                new[] { NewTransfer(wizardId, 1), NewTransfer(wizardId, 1), NewTransfer(998877UL, 1), NewTransfer(887766UL, 2) },
            ])!;
            Equal(2, owners.Length, "same-room private loads are fanned out once per owner");
            Check(owners.Contains(wizardId) && owners.Contains(998877UL), "same-room private loads retain each distinct instance owner");

            var takeTransfers = worldType.GetMethod("TakeTransfersForZone", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing GameWorld.TakeTransfersForZone");
            var queuedTransfers = new Dictionary<ZONE_102_PROTOCOL.MSG_ZONETRANSFER, IActorRef> {
                [NewTransfer(wizardId, 1)] = ActorRefs.Nobody,
                [NewTransfer(998877UL, 1)] = ActorRefs.Nobody,
            };
            var dispatched = (Array)(takeTransfers.Invoke(null, [queuedTransfers, TowerRoomPath(1)])
                ?? throw new InvalidOperationException("Could not snapshot shared zone transfers"));
            Equal(2, dispatched.Length, "one shared load dispatches every queued owner transfer");
            Equal(0, queuedTransfers.Count, "shared load removes only after taking a stable transfer snapshot");

            var zoneStaleCheck = containerType.GetMethod("IsTowerZoneStale", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing InstanceContainer.IsTowerZoneStale");
            Check((bool)zoneStaleCheck.Invoke(null, ["same-encounter", 1L, "same-encounter", 2L])!,
                "a loaded tower room from a previous authority epoch must be retired");
            Check(!(bool)zoneStaleCheck.Invoke(null, ["same-encounter", 2L, "same-encounter", 2L])!,
                "a loaded tower room with the current encounter and epoch can be reused");

            Console.WriteLine("PASS: private tower routing persists manifests, remaps floor 6, fans out owner loads, and retires/reloads stale encounter and authority epoch actors");
        } finally {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database, hardDelete: true));
        }
    }

    private static ZONE_102_PROTOCOL.MSG_ZONETRANSFER NewTransfer(ulong wizardId, int requestedFloor)
        => new() {
            DestinationZone = TowerRoomPath(requestedFloor),
            DestinationLocation = "Start",
            OwnerCharId = wizardId,
            IsPrivate = true,
        };

    private static void LoadZone(IActorRef container, int floor) {
        var createdBeforeLoad = TestLoadedZoneActor.Created;
        var zoneDataType = typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS).GetField("ZoneData")?.FieldType
            ?? throw new InvalidOperationException("MSG_ZONELOADRESULTS.ZoneData field is missing");
        var zoneData = Activator.CreateInstance(zoneDataType)
            ?? throw new InvalidOperationException("Could not create test zone data");
        Set(zoneData, "m_zoneName", TowerRoomPath(floor));
        var message = new ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS { ZonePath = TowerRoomPath(floor) };
        Set(message, "ZoneData", zoneData);
        container.Tell(message);
        WaitUntil(() => TestLoadedZoneActor.Created > createdBeforeLoad, $"room actor for floor {floor} was created");
    }

    private static void WaitUntil(Func<bool> condition, string message) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) { Thread.Sleep(10); }
        Check(condition(), message);
    }

    private static void InitializeLogging() {
        var directory = Path.Combine(Path.GetTempPath(), "tower-actor-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "test.ini");
        File.WriteAllText(config,
            $"[Logging]\nLogPath={directory}/test.log\nLogLevel=INFO\nLogFormat={{Message}}{{NewLine}}\nSeqSinkUrl=http://127.0.0.1:1\n[Tower]\nEnabled=true\n");
        ConfigurationManager.Initialize(config);
    }

    private static object AskRoute(IActorRef container, object query)
        => container.Ask<object>(query, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("InstanceContainer returned no route response");

    private static object AskOrderedRoute(ActorSystem system, IActorRef container, object priorMessage, object query) {
        var sender = system.ActorOf(Props.Create(() => new OrderedRouteAsker(container, priorMessage, query)));
        return sender.Ask<object>(new OrderedRouteAsker.Start(), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("Ordered InstanceContainer route returned no response");
    }

    private static string TowerRoomPath(int floor)
        => $"WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_{((floor - 1) % 5) + 1}";

    private static Type RequireType(string fullName)
        => CoreAssembly.GetType(fullName) ?? throw new InvalidOperationException($"Missing type {fullName}");

    private static object Call(object target, string methodName, params object[] args) {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {target.GetType().Name}.{methodName}");
        return method.Invoke(target, args) ?? throw new InvalidOperationException($"{methodName} returned null");
    }

    private static object GetProperty(object target, string name) {
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null) {
            return property.GetValue(target)
                ?? throw new InvalidOperationException($"Null {type.Name}.{name}");
        }

        return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target)
            ?? throw new InvalidOperationException($"Missing {type.Name}.{name}");
    }

    private static string StringProperty(object target, string name) => Convert.ToString(GetProperty(target, name));
    private static long LongProperty(object target, string name) => Convert.ToInt64(GetProperty(target, name));

    private static void Set(object target, string name, object value) {
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null) {
            property.SetValue(target, value);
            return;
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {type.Name}.{name}");
        field.SetValue(target, value);
    }

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class TestLoadedZoneActor : ReceiveActor {
        private static readonly ConcurrentQueue<int> s_stops = new();
        private static int s_created;
        private static IActorRef s_latest;

        public static int Created => Volatile.Read(ref s_created);
        public static int Stopped => s_stops.Count;

        public TestLoadedZoneActor() {
            Interlocked.Increment(ref s_created);
            Volatile.Write(ref s_latest, Self);
            Receive<ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS>(_ => { });
            Receive<Fail>(_ => Context.Stop(Self));
        }

        public static void StopLatest()
            => Volatile.Read(ref s_latest)?.Tell(new Fail());

        public static void Reset() {
            Interlocked.Exchange(ref s_created, 0);
            while (s_stops.TryDequeue(out _)) { }
        }

        protected override void PostStop() {
            s_stops.Enqueue(1);
            base.PostStop();
        }

        private sealed class Fail { }
    }

    private sealed class OrderedRouteAsker : ReceiveActor {
        private readonly IActorRef _container;
        private readonly object _priorMessage;
        private readonly object _query;
        private IActorRef _replyTo;

        public sealed class Start { }

        public OrderedRouteAsker(IActorRef container, object priorMessage, object query) {
            _container = container;
            _priorMessage = priorMessage;
            _query = query;
            Receive<Start>(_ => {
                _replyTo = Sender;
                _container.Tell(_priorMessage, Self);
                _container.Tell(_query, Self);
            });
            ReceiveAny(message => {
                _replyTo?.Tell(message, Self);
                Context.Stop(Self);
            });
        }
    }
}
