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
 * GAME WORLD MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Provides comprehensive zone management and player transfer functionality
 * using Akka.NET actor system for distributed zone loading and instancing.
 * 
 * USAGE EXAMPLE:
 * Create GameWorld actor using GameWorld.Props(gameServer)
 * Handle zone transfers and dynamic zone creation
 * 
 * NOTE:
 * Utilizes Akka.NET actor system for scalable world management.
 * Supports dynamic zone loading, instancing, and transfer mechanisms.
 * Implements timeout and error handling for zone loading.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/13/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.Util.Internal;
using Imcodec.Cryptography;
using Imlight.Common;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Manages game world zone creation, loading, and player transfers.
/// </summary>
public class GameWorld : ReceiveProtocolDispatcher, IWithTimers {

    private const int LOAD_ZONE_TIMEOUT_IN_SECONDS = 15;
    private const int HARD_LIMIT_INSTANCE_THRESHHOLD = 12; // Raids are 12 players.
    private const string LOAD_ZONE_FAILURE_ERROR = "ERROR_EnterWorldFailed";
    private const string ZONE_DOES_NOT_EXIST_ERROR = "ERROR_FailedToCreate";
    private const string TOWER_OWNERSHIP_TIMER = "tower-server-ownership";

    public ITimerScheduler Timers { get; set; }

    private readonly Dictionary<string, IActorRef> _publicZones = [];
    private readonly List<uint> _dynamicZoneIds = [];
    private readonly Dictionary<ZONE_102_PROTOCOL.MSG_ZONETRANSFER, IActorRef> _awaitingTransfers = [];
    private readonly Dictionary<string, IActorRef> _zoneLoaderActors = [];
    private readonly GameServer _server;

    private readonly Dictionary<ulong, IActorRef> _instanceContainers = [];
    private readonly Dictionary<string, (ulong Owner, bool Private)> _instanceCreationCalledByMap = [];
    private readonly bool _towerEnabled = ConfigurationManager.GetValue("Tower.Enabled", false);
    private TowerRunCollection _towerRuns;
    private TowerServerOwnership _towerOwnership;

    // ctor
    public GameWorld(GameServer server) {
        _server = server;

        // Preload NPC vendor data and trainer data.
        NpcInventoryCollection.PreloadInventories();
        NpcSpellInventoryCollection.PreloadNpcSpellInventories();
        CreatureSpellbookCollection.PreloadSpellbooks();
        NpcDropTableCollection.PreloadDropTables();
    }

    public static Props Props(GameServer server)
        => Akka.Actor.Props.Create(() => new GameWorld(server));

    protected override void PreStart() {
        base.PreStart();
        if (!_towerEnabled) { return; }

        try {
            _towerOwnership = TowerServerOwnership.RegisterProcessOwner(PlayerDatabase.Instance.Store);
            if (!_towerOwnership.TryAcquire()) {
                Logger.Warning("Golem Tower is unavailable because another server process owns the Tower lease.");
            }
            Timers.StartPeriodicTimer(TOWER_OWNERSHIP_TIMER, new TowerOwnershipHeartbeatMessage(),
                TowerServerOwnership.HeartbeatInterval, TowerServerOwnership.HeartbeatInterval);
        }
        catch (Exception ex) {
            Logger.Error("Could not initialize Golem Tower server ownership: {Reason}", Logger.Args(ex.Message));
        }
    }

    protected override void PostStop() {
        Timers?.Cancel(TOWER_OWNERSHIP_TIMER);
        if (_towerOwnership is not null) {
            TowerServerOwnership.UnregisterProcessOwner(_towerOwnership);
            _towerOwnership = null;
        }
        base.PostStop();
    }

    [InternalMessageHandler(typeof(TowerOwnershipHeartbeatMessage))]
    private void ReceiveTowerOwnershipHeartbeat(TowerOwnershipHeartbeatMessage message) {
        if (_towerOwnership?.Renew() != true) {
            Logger.Warning("Golem Tower server ownership is not held; Tower routes and writes will be rejected.");
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    private void ReceiveZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        // First, make sure this zone is valid by checking the AccessPassManager.
        if (!AccessPassManager.DoesZoneExist(message.DestinationZone)) {
            Logger.Error("{Name} received invalid zone name {ZoneName}",
                Logger.Args(nameof(GameWorld), message.DestinationZone));

            var response = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorCode = StringHash.Compute(ZONE_DOES_NOT_EXIST_ERROR)
            };
            Sender.Tell(response);

            return;
        }

        // The Court is always shared; only physical Tower rooms are privately instantiated.
        var isCourt = _towerEnabled
            && string.Equals(message.DestinationZone, TowerZonePaths.EntryZone, StringComparison.OrdinalIgnoreCase);
        if (isCourt) {
            message = TowerTransferPolicy.Copy(message);
            message.IsPrivate = false;
            HandleOtherZoneTransfer(message);
            return;
        }

        var isTowerRoom = TowerTransferPolicy.IsEnabledTowerRoomRequest(message, _towerEnabled);
        if (isTowerRoom) {
            if (message.OwnerCharId == 0) {
                Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                    ErrorCode = StringHash.Compute(LOAD_ZONE_FAILURE_ERROR),
                    ErrorMessage = "Tower transfer requires an authenticated owner.",
                });
                return;
            }
            message = TowerTransferPolicy.Copy(message);
            message.IsPrivate = true;
        }

        var hasContainer = _instanceContainers.TryGetValue(message.OwnerCharId, out var instanceContainer);
        TowerRunState towerRunToActivate = null;
        if (isTowerRoom && _towerOwnership?.TryAcquire() != true) {
            Logger.Warning("Rejected Golem Tower route for wizard {WizardId} because this process does not own the Tower lease.",
                Logger.Args(message.OwnerCharId));
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorMessage = "The Golem Tower is temporarily unavailable. Please retry shortly.",
                ErrorCode = StringHash.Compute(LOAD_ZONE_FAILURE_ERROR),
            });
            return;
        }
        if (isTowerRoom) {
            try {
                _towerRuns ??= new TowerRunCollection(PlayerDatabase.Instance.Store);
                towerRunToActivate = _towerRuns.CreateOrResume(
                    message.OwnerCharId,
                    RandomGen.GenerateHash(Guid.NewGuid().ToString("N")));
            }
            catch (Exception ex) {
                Logger.Error("Could not open a Golem Tower run for wizard {WizardId}: {Reason}",
                    Logger.Args(message.OwnerCharId, ex.Message));
                Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                    ErrorMessage = "The Golem Tower run could not be opened. Please retry entry.",
                    ErrorCode = StringHash.Compute(LOAD_ZONE_FAILURE_ERROR),
                });
                return;
            }
            if (towerRunToActivate is null) {
                Logger.Warning("Golem Tower run for wizard {WizardId} is already owned by another active session.",
                    Logger.Args(message.OwnerCharId));
                Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                    ErrorMessage = "The Golem Tower run is already owned by another active session.",
                    ErrorCode = StringHash.Compute(LOAD_ZONE_FAILURE_ERROR),
                });
                return;
            }

            Logger.Information("Golem Tower run {RunId} opened for wizard {WizardId} at floor {Floor}, attempt generation {AttemptGeneration}.",
                Logger.Args(towerRunToActivate.RunId, towerRunToActivate.OwnerWizardId,
                    towerRunToActivate.CurrentFloor, towerRunToActivate.AttemptGeneration));

        }

        // If IsPrivate is set but no instance container exists, create one proactively.
        if (message.IsPrivate && !hasContainer) {
            instanceContainer = CreateInstanceContainer(message.OwnerCharId);
            hasContainer = true;
        }

        // If this owner has an instance container, we'll first check with it to see if it has the zone loaded.
        // If it does, we'll forward the transfer request to it.
        if (hasContainer) {
            var isTowerTransfer = isTowerRoom;

            // A normal fresh SIGIL entry drops the old instance. Tower room retirement is handled by the
            // run-aware container query so its stale actor can terminate before the same room name is reused.
            if (message.ResetInstance && !isTowerTransfer) {
                instanceContainer.Tell(new ZONE_102_PROTOCOL.MSG_DROPINSTANCEZONE {
                    ZoneName = message.DestinationZone,
                });
            }

            HandleInstancedZoneTransfer(message, towerRunToActivate);
        }
        else {
            HandleOtherZoneTransfer(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADTIMER))]
    private void ReceiveZoneTimerEnd(ZONE_102_PROTOCOL.MSG_ZONELOADTIMER message) {
        // If the timer is reached, the zone did not load within the timeout.
        if (_zoneLoaderActors.TryGetValue(message.ZonePath, out var loaderRef)) {
            _zoneLoaderActors.Remove(message.ZonePath);
            Context.Stop(loaderRef);

            Logger.Error("Failed to load zone {ZoneName} within the timeout",
                Logger.Args(message.ZonePath));
        }

        // Reply to any zone transfer requests with failure.
        var reply = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
            ErrorMessage = "Failed to load zone within the timeout",
            ErrorCode = StringHash.Compute(LOAD_ZONE_FAILURE_ERROR)
        };
        var transfers = TakeTransfersForZone(_awaitingTransfers, message.ZonePath);
        foreach (var (transferMsg, transferActor) in transfers) {
            transferActor.Tell(reply);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    private void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        // If the zone failed to load, inform anyone waiting for the zone to load.
        if (message.Error) {
            Logger.Error("Failed to load zone: {ErrorMessage}",
                Logger.Args(message.ErrorMessage));

            // Reply to any zone transfer requests with failure.
            var reply = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorMessage = message.ErrorMessage,
                ErrorCode = StringHash.Compute(LOAD_ZONE_FAILURE_ERROR)
            };
            var transfers = TakeTransfersForZone(_awaitingTransfers, message.ZonePath);
            foreach (var (transferMsg, transferActor) in transfers) {
                transferActor.Tell(reply);
            }

            RemoveZoneLoader(message.ZonePath);

            return;
        }

        // The game world has no obligation to validate the zone data.
        // If the zone data is invalid, the zone actor will handle it, and the game world will be notified
        // with a `MSG_ZONECLOSED` message.
        var zonePath = message.ZonePath;

        // Search for the user who called for the creation of this zone.
        var (ownerId, wasPrivateRequest) = _instanceCreationCalledByMap[zonePath];
        _instanceCreationCalledByMap.Remove(zonePath);

        // Determine if this zone is instanced. It is instanced when ANY of:
        //   - the triggering transfer explicitly asked for a PRIVATE instance (a sigil dungeon / solo
        //     entry); WITHOUT this, a dungeon whose hard limit is > the raid threshold loads as a shared
        //     PUBLIC zone, so two independent owners collapse into one copy;
        //   - its hard limit is small enough to be an instance (normal dungeons).
        var isInstancedZone = wasPrivateRequest
                           || message.ZoneData.m_nHardLimit <= HARD_LIMIT_INSTANCE_THRESHHOLD;
        if (isInstancedZone) {
            // A zone loader can be shared while multiple owners request the same private room.
            // Each owner still needs its own instance actor; sending the load result only to the
            // first owner would leave the other queued transfers pointing at empty containers.
            var ownersWaitingForZone = SelectInstancedLoadOwners(zonePath, ownerId, _awaitingTransfers.Keys);

            foreach (var instanceOwnerId in ownersWaitingForZone) {
                if (!_instanceContainers.TryGetValue(instanceOwnerId, out var instanceContainer)) {
                    instanceContainer = CreateInstanceContainer(instanceOwnerId);
                }

                // Each private owner creates its own zone actor from this shared, read-only load result.
                instanceContainer.Tell(message);
            }

            ProcessTransfersForInstanceContainer(zonePath);
        }
        else {
            // Create the new public zone and inform it of the load results.
            _publicZones[zonePath] = CreateZone(zonePath);
            _publicZones[zonePath].Tell(message);

            ProcessTransfersForPublicZone(zonePath);
        }

        RemoveZoneLoader(zonePath);
    }

    internal static ulong[] SelectInstancedLoadOwners(
        string zonePath,
        ulong fallbackOwnerId,
        IEnumerable<ZONE_102_PROTOCOL.MSG_ZONETRANSFER> pendingTransfers)
        => (pendingTransfers ?? [])
            .Where(transfer => string.Equals(transfer.DestinationZone, zonePath, StringComparison.OrdinalIgnoreCase))
            .Select(transfer => transfer.OwnerCharId)
            .Append(fallbackOwnerId)
            .Distinct()
            .ToArray();

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONECLOSED))]
    private void ReceiveZoneClosed(ZONE_102_PROTOCOL.MSG_ZONECLOSED message) {
        var dynamicId = message.DynamicZoneId;
        _dynamicZoneIds.Remove(dynamicId);
    }

    private static string SanitizeZoneName(string zoneName)
        => zoneName.Replace('/', '-');

    private void HandleInstancedZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message,
                                             TowerRunState towerRunToActivate = null) {
        var isTowerTransfer = TowerTransferPolicy.IsEnabledTowerRoomRequest(message, _towerEnabled);

        // A PRIVATE transfer (sigil dungeon / solo entry) must NEVER be satisfied by a shared public copy;
        // that would drop the player into a public instance of a dungeon they asked to enter privately.
        if (!message.IsPrivate && !isTowerTransfer && _publicZones.TryGetValue(message.DestinationZone, out var zone)) {
            zone.Forward(message);

            return;
        }

        // Capture sender before async work; Akka Sender is context-bound.
        var originalSender = Sender;

        if (isTowerTransfer) {
            _instanceContainers[message.OwnerCharId]
                .Ask<TowerInstanceRouteResult>(new TowerInstanceRouteQuery {
                    Transfer = message,
                    ActivateRun = towerRunToActivate,
                }, TimeSpan.FromSeconds(5))
                .ContinueWith(task => new TowerInstanceRouteCompleted {
                    OriginalSender = originalSender,
                    OriginalTransfer = message,
                    Response = task.IsFaulted || task.IsCanceled ? null : task.Result,
                })
                .PipeTo(Self);
            return;
        }

        var hasZoneMsg = new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONE {
            ZoneName = message.DestinationZone
        };
        var timeOut = TimeSpan.FromSeconds(5);

        _instanceContainers[message.OwnerCharId]
            .Ask<ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONERSP>(hasZoneMsg, timeOut)
            .ContinueWith(t => new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINER_QUERY_RESULT {
                OriginalSender = originalSender,
                OriginalTransfer = message,
                Response = t.IsFaulted || t.IsCanceled ? null : t.Result
            })
            .PipeTo(Self);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_INSTANCECONTAINER_QUERY_RESULT))]
    private void ReceiveInstanceContainerQueryResult(ZONE_102_PROTOCOL.MSG_INSTANCECONTAINER_QUERY_RESULT result) {
        var message = result.OriginalTransfer;

        if (result.Response == null || !result.Response.HasZone) {
            // A loader may already be in progress; a second player can enter the same instance zone while it
            // is still loading. Only spin up one loader (and register its owner once); the extra transfer just
            // queues and is handled when the zone finishes loading. These results arrive one at a time on the
            // GameWorld actor thread, so even though the Asks ran concurrently, this guard still prevents a
            // duplicate loader or a duplicate owner registration.
            if (!_zoneLoaderActors.ContainsKey(message.DestinationZone)) {
                CreateZoneLoader(message.DestinationZone);
                _instanceCreationCalledByMap.Add(message.DestinationZone, (message.OwnerCharId, message.IsPrivate));
            }
            _awaitingTransfers.Add(message, result.OriginalSender);

            return;
        }

        // Instance container has the zone; forward the transfer to it.
        _instanceContainers[message.OwnerCharId].Tell(message, result.OriginalSender);
    }

    [MessageHandler(typeof(TowerInstanceRouteCompleted))]
    private void ReceiveTowerInstanceRouteCompleted(TowerInstanceRouteCompleted result) {
        if (result.Response is null || !string.IsNullOrEmpty(result.Response.ErrorMessage)) {
            result.OriginalSender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorMessage = result.Response?.ErrorMessage ?? "The Golem Tower instance did not respond.",
                ErrorCode = StringHash.Compute(LOAD_ZONE_FAILURE_ERROR),
            });
            return;
        }

        var transfer = result.Response.Transfer;
        if (!result.Response.HasZone) {
            if (!_zoneLoaderActors.ContainsKey(transfer.DestinationZone)) {
                CreateZoneLoader(transfer.DestinationZone);
                _instanceCreationCalledByMap.AddOrSet(transfer.DestinationZone,
                    (transfer.OwnerCharId, transfer.IsPrivate));
            }
            _awaitingTransfers.Add(transfer, result.OriginalSender);
            return;
        }

        _instanceContainers[transfer.OwnerCharId].Tell(transfer, result.OriginalSender);
    }

    public void HandleOtherZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        // Get the zone if it's already loaded; or, create a new one if it's not.
        IActorRef zone;
        if (!_publicZones.TryGetValue(message.DestinationZone, out var value)) {
            zone = CreateZoneLoader(message.DestinationZone);

            // We want to wait until the zone is fully loaded before transferring the player.
            _awaitingTransfers.Add(message, Sender);
            _instanceCreationCalledByMap.AddOrSet(message.DestinationZone, (message.OwnerCharId, message.IsPrivate));
        }
        else {
            // If the zone is already loaded, we can transfer the player immediately.
            zone = value;
            zone.Forward(message);
        }
    }

    private IActorRef CreateZoneLoader(string zonePath) {
        // Run the loader on the dedicated zone-loading dispatcher (see akka.conf). Its work, a synchronous WAD
        // open plus six deserializes, would otherwise run on the default dispatcher, where a burst of
        // concurrent loads can starve session and handshake processing and drop entering players. Fail-safe:
        // if the dispatcher cannot be resolved, fall back to the default rather than failing every zone load.
        IActorRef loaderRef;
        try {
            loaderRef = Context.ActorOf(
                Akka.Actor.Props.Create(() => new ZoneLoader()).WithDispatcher("zone-loading-dispatcher"));
        }
        catch (Exception ex) {
            Logger.Warning("zone-loading-dispatcher unavailable ({Reason}); loading '{ZoneName}' on the default " +
                "dispatcher instead.", Logger.Args(ex.Message, zonePath));
            loaderRef = Context.ActorOf(Akka.Actor.Props.Create(() => new ZoneLoader()));
        }

        Logger.Information("Game world starting zone load: {ZoneName}", Logger.Args(zonePath));

        // Tell the loader to begin loading the zone and await the response.
        var msg = new ZONE_102_PROTOCOL.MSG_ZONELOADBEGIN { ZonePath = zonePath };
        loaderRef.Tell(msg);

        _zoneLoaderActors.Add(zonePath, loaderRef);

        // Send a message to ourselves to clean up the loader actor after a certain amount of time.
        // Receiving `MSG_ZONELOADRESULTS` from the loader actor will cancel this timer.
        // This means that receiving `MSG_ZONELOADTIMER` will only happen if the zone failed to load.
        var loadZoneTimeoutKey = $"loadZoneTimeout_{zonePath}";
        var loadZoneTimeoutTimespan = TimeSpan.FromSeconds(LOAD_ZONE_TIMEOUT_IN_SECONDS);
        var loadZoneTimeoutMsg = new ZONE_102_PROTOCOL.MSG_ZONELOADTIMER { ZonePath = zonePath };
        Timers.StartSingleTimer(loadZoneTimeoutKey, loadZoneTimeoutMsg, loadZoneTimeoutTimespan);

        return loaderRef;
    }

    private IActorRef CreateZone(string zoneName) {
        var zoneActorName = SanitizeZoneName(zoneName);
        var zoneId = GetNextDynamicZoneId();
        var zone = Context.ActorOf(Zone.Core.Zone.Props(zoneName, zoneId), zoneActorName);

        // Log the new zone creation.
        Logger.Information("Game world created new zone: {ZoneName}",
            Logger.Args(zoneName));

        return zone;
    }

    private IActorRef CreateInstanceContainer(ulong ownerId) {
        var zoneActorName = $"{nameof(InstanceContainer)}_{ownerId}"; // Append the owner ID to the zone name.

        var instanceContainer = Context.ActorOf(InstanceContainer.Props(ownerId), zoneActorName);
        _instanceContainers.Add(ownerId, instanceContainer);

        // Log the new instance container creation.
        Logger.Information("Game world creates new instance container {0}, owned by {1}",
            Logger.Args(zoneActorName, ownerId));

        return instanceContainer;
    }

    private void RemoveZoneLoader(string zonePath) {
        // Stop the timer.
        var loadZoneTimeoutKey = $"loadZoneTimeout_{zonePath}";
        Timers.Cancel(loadZoneTimeoutKey);

        if (zonePath is not null) {
            if (_zoneLoaderActors.TryGetValue(zonePath, out var loaderRef)) {
                _zoneLoaderActors.Remove(zonePath);
                Context.Stop(loaderRef);
            }
        }
    }

    private void ProcessTransfersForPublicZone(string zonePath) {
        var transfers = TakeTransfersForZone(_awaitingTransfers, zonePath);
        if (transfers.Length == 0) {
            Logger.Error("{Name} received unexpected zone load result for {ZoneName}",
                Logger.Args(nameof(GameWorld), zonePath));

            return;
        }

        foreach (var (transferMsg, transferActor) in transfers) {
            _publicZones[zonePath].Tell(transferMsg, transferActor);
        }
    }

    private void ProcessTransfersForInstanceContainer(string zonePath) {
        var transfers = TakeTransfersForZone(_awaitingTransfers, zonePath);
        if (transfers.Length == 0) {
            Logger.Error("{Name} received unexpected zone load result for {ZoneName}",
                Logger.Args(nameof(GameWorld), zonePath));

            return;
        }

        foreach (var (transferMsg, transferActor) in transfers) {
            _instanceContainers[transferMsg.OwnerCharId].Tell(transferMsg, transferActor);
        }
    }

    internal static KeyValuePair<ZONE_102_PROTOCOL.MSG_ZONETRANSFER, IActorRef>[] TakeTransfersForZone(
        Dictionary<ZONE_102_PROTOCOL.MSG_ZONETRANSFER, IActorRef> awaitingTransfers,
        string zonePath) {
        var transfers = awaitingTransfers
            .Where(transfer => string.Equals(transfer.Key.DestinationZone, zonePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var transfer in transfers) { awaitingTransfers.Remove(transfer.Key); }
        return transfers;
    }

    private uint GetNextDynamicZoneId() {
        uint id;
        var random = new Random();
        do {
            id = (uint) random.Next(1, int.MaxValue);
        } while (_dynamicZoneIds.Contains(id));

        _dynamicZoneIds.Add(id);

        return id;
    }

}
