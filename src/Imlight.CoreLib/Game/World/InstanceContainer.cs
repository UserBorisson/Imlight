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
 * INSTANCE CONTAINER MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages zone instances for a specific player using Akka.NET actor system,
 * handling zone loading, transfer, and dynamic zone creation.
 * 
 * USAGE EXAMPLE:
 * Create InstanceContainer using InstanceContainer.Props(ownerId)
 * Handle zone transfers and dynamic zone loading for a specific player
 * 
 * NOTE:
 * Utilizes Akka.NET actor system for per-player zone instancing.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Manages zone instances for a specific player using Akka.NET actor system.
/// </summary>
internal sealed class InstanceContainer(
    ulong instanceOwnerId,
    TowerRunCollection towerRuns = null,
    Func<string, uint, TowerRunState, Props> zonePropsFactory = null)
    : ReceiveProtocolDispatcher, IWithTimers {

    public ITimerScheduler Timers { get; set; }

    private readonly ulong _instanceOwnerId = instanceOwnerId;
    private readonly List<uint> _dynamicZoneIds = [];
    private readonly Dictionary<string, IActorRef> _zones = [];
    private readonly Dictionary<string, string> _zoneEncounterIds = [];
    private readonly Dictionary<string, long> _zoneAuthorityEpochs = [];
    private readonly Dictionary<IActorRef, string> _retiringZones = [];
    private readonly Dictionary<string, ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS> _loadsWaitingForRetirement = [];
    private readonly Dictionary<string, List<(ZONE_102_PROTOCOL.MSG_ZONETRANSFER Transfer, IActorRef Sender)>> _transfersWaitingForRetirement = [];
    private TowerRunCollection _towerRuns = towerRuns;
    private TowerRunState _towerRun;
    private readonly Func<string, uint, TowerRunState, Props> _zonePropsFactory = zonePropsFactory;

    public static Props Props(ulong instanceOwnerId) 
        => Akka.Actor.Props.Create(() => new InstanceContainer(instanceOwnerId));

    internal static Props Props(ulong instanceOwnerId, TowerRunCollection towerRuns)
        => Akka.Actor.Props.Create(() => new InstanceContainer(instanceOwnerId, towerRuns));

    internal static Props Props(
        ulong instanceOwnerId,
        TowerRunCollection towerRuns,
        Func<string, uint, TowerRunState, Props> zonePropsFactory)
        => Akka.Actor.Props.Create(() => new InstanceContainer(instanceOwnerId, towerRuns, zonePropsFactory));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    public void ReceiveZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        Logger.Debug("Container (owned by: {0}) received zone transfer request for zone: {1}",
            Logger.Args(_instanceOwnerId, message.DestinationZone));

        if (!_zones.TryGetValue(message.DestinationZone, out var zone)) {
            if (_retiringZones.ContainsValue(message.DestinationZone)) {
                if (!_transfersWaitingForRetirement.TryGetValue(message.DestinationZone, out var pending)) {
                    pending = [];
                    _transfersWaitingForRetirement[message.DestinationZone] = pending;
                }
                pending.Add((message, Sender));
                return;
            }

            Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorMessage = $"Zone {message.DestinationZone} is not loaded in instance {_instanceOwnerId}",
                ErrorCode = 1,
            });
            return;
        }

        // Otherwise, forward this transfer message to the zone.
        zone.Forward(message);
    }

    [MessageHandler(typeof(TowerInstanceRouteQuery))]
    public void ReceiveTowerInstanceRouteQuery(TowerInstanceRouteQuery message) {
        if (message.ActivateRun is not null) {
            if (message.ActivateRun.OwnerWizardId != _instanceOwnerId) {
                Sender.Tell(new TowerInstanceRouteResult { ErrorMessage = "Tower run owner does not match instance owner." });
                return;
            }
            if (ShouldAcceptTowerRunState(_towerRun, message.ActivateRun)) {
                _towerRun = message.ActivateRun;
            }
        }

        var transfer = message.Transfer is null ? null : TowerTransferPolicy.Copy(message.Transfer);
        if (transfer is null || transfer.OwnerCharId != _instanceOwnerId) {
            Sender.Tell(new TowerInstanceRouteResult { ErrorMessage = "Tower transfer owner does not match instance owner." });
            return;
        }

        if (TowerZonePaths.TryGetRoomNumber(transfer.DestinationZone, out _)) {
            if (_towerRun is null || _towerRun.Status != TowerRunStatus.Active) {
                Sender.Tell(new TowerInstanceRouteResult {
                    Transfer = transfer,
                    ErrorMessage = "There is no active Golem Tower run for this instance.",
                });
                return;
            }

            if (!TryEnsureManifest(_towerRun, out var manifest)
                || !TowerZonePolicy.IsValidManifest(_towerRun, TowerZonePaths.GetRoomPath(_towerRun.CurrentFloor))) {
                Sender.Tell(new TowerInstanceRouteResult {
                    Transfer = transfer,
                    ErrorMessage = "The Golem Tower encounter could not be saved. Please retry entry.",
                });
                return;
            }

            transfer.DestinationZone = TowerZonePaths.GetRoomPath(_towerRun.CurrentFloor);
            if (_zones.ContainsKey(transfer.DestinationZone)
                && (!_zoneEncounterIds.TryGetValue(transfer.DestinationZone, out var loadedEncounter)
                    || !_zoneAuthorityEpochs.TryGetValue(transfer.DestinationZone, out var loadedEpoch)
                    || IsTowerZoneStale(loadedEncounter, loadedEpoch, manifest.EncounterId, _towerRun.AuthorityEpoch))) {
                DropInstanceZone(transfer.DestinationZone);
            }
        }

        if (transfer.ResetInstance) {
            DropInstanceZone(transfer.DestinationZone);
            transfer.ResetInstance = false;
        }

        Sender.Tell(new TowerInstanceRouteResult {
            Transfer = transfer,
            ResolvedDestinationZone = transfer.DestinationZone,
            ResolvedDestinationLocation = transfer.DestinationLocation,
            HasZone = _zones.ContainsKey(transfer.DestinationZone),
        });
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    public void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        var zoneName = message.ZoneData.m_zoneName;
        if (_retiringZones.Values.Contains(zoneName, StringComparer.OrdinalIgnoreCase)) {
            _loadsWaitingForRetirement[zoneName] = message;
            return;
        }

        if (_zones.ContainsKey(zoneName)) {
            DropInstanceZone(zoneName);
            _loadsWaitingForRetirement[zoneName] = message;
            return;
        }

        if (TowerZonePolicy.IsEnabledTowerRoom(zoneName)
            && !TowerZonePolicy.IsValidManifest(_towerRun, zoneName)) {
            Logger.Warning("Rejected Tower room {ZoneName}: no valid persisted owner manifest.", Logger.Args(zoneName));
            // No Zone actor is created; GameWorld's pending transfers receive the normal missing-zone error.
            return;
        }

        CreateAndLoadZone(zoneName, message);
    }

    private void CreateAndLoadZone(string zoneName, ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        if (TowerZonePolicy.IsEnabledTowerRoom(zoneName)
            && !TowerZonePolicy.IsValidManifest(_towerRun, zoneName)) {
            Logger.Warning("Rejected invalid Tower room {ZoneName} at creation.", Logger.Args(zoneName));
            return;
        }
        var zoneActor = CreateZone(zoneName);
        zoneActor.Tell(message);

        _zones[zoneName] = zoneActor;
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONE))]
    public void ReceiveInstanceContainerHasZone(ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONE message) 
        => Sender.Tell(new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONERSP {
            HasZone = _zones.ContainsKey(message.ZoneName)
        });

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DROPINSTANCEZONE))]
    public void ReceiveDropInstanceZone(ZONE_102_PROTOCOL.MSG_DROPINSTANCEZONE message) {
        DropInstanceZone(message.ZoneName);
    }

    [MessageHandler(typeof(TowerRunChanged))]
    public void ReceiveTowerRunChanged(TowerRunChanged message) {
        if (message.State?.OwnerWizardId == _instanceOwnerId
            && ShouldAcceptTowerRunState(_towerRun, message.State)) {
            _towerRun = message.State;
        }
    }

    internal static bool ShouldAcceptTowerRunState(TowerRunState current, TowerRunState incoming) {
        if (incoming is null) { return false; }
        if (current is null) { return true; }
        if (incoming.OwnerWizardId != current.OwnerWizardId) { return false; }

        if (string.Equals(current.RunId, incoming.RunId, StringComparison.Ordinal)) {
            if (incoming.AuthorityEpoch != current.AuthorityEpoch) {
                return incoming.AuthorityEpoch > current.AuthorityEpoch;
            }
            return incoming.Revision >= current.Revision;
        }

        return incoming.CreatedUtc > current.CreatedUtc;
    }

    [MessageHandler(typeof(Terminated))]
    private void ReceiveZoneTerminated(Terminated message) {
        if (!_retiringZones.Remove(message.ActorRef, out var zoneName)) { return; }

        if (_loadsWaitingForRetirement.Remove(zoneName, out var load)) {
            CreateAndLoadZone(zoneName, load);
        }

        if (_transfersWaitingForRetirement.Remove(zoneName, out var transfers)
            && _zones.TryGetValue(zoneName, out var zone)) {
            foreach (var (transfer, originalSender) in transfers) {
                zone.Tell(transfer, originalSender);
            }
        }
    }

    private IActorRef CreateZone(string zoneName) {
        var zoneActorName = SanitizeZoneName(zoneName);
        var zoneId = GetNextDynamicZoneId();
        TowerRunState zoneRun = null;
        if (_towerRun?.Status == TowerRunStatus.Active
            && TowerZonePaths.TryGetRoomNumber(zoneName, out var roomNumber)
            && TryEnsureManifest(_towerRun, out var manifest)
            && manifest.PhysicalRoomNumber == roomNumber) {
            zoneRun = _towerRun;
            _zoneEncounterIds[zoneName] = manifest.EncounterId;
            _zoneAuthorityEpochs[zoneName] = zoneRun.AuthorityEpoch;
            Logger.Information("Golem Tower run {RunId} creates floor {Floor}, attempt generation {AttemptGeneration}, encounter {EncounterId} in {ZoneName}.",
                Logger.Args(zoneRun.RunId, zoneRun.CurrentFloor, zoneRun.AttemptGeneration, manifest.EncounterId, zoneName));
        }
        if (TowerZonePolicy.IsEnabledTowerRoom(zoneName)
            && !TowerZonePolicy.IsValidManifest(zoneRun, zoneName)) {
            throw new InvalidOperationException("A Tower room cannot be created without a valid manifest.");
        }
        var zoneProps = _zonePropsFactory?.Invoke(zoneName, zoneId, zoneRun)
            ?? Zone.Core.Zone.Props(zoneName, zoneId, zoneRun);
        var zone = Context.ActorOf(zoneProps, zoneActorName);

        // Log the new zone creation.
        Logger.Information("Game world created new zone: {ZoneName}",
            Logger.Args(zoneName));

        return zone;
    }

    private void DropInstanceZone(string zoneName) {
        if (!_zones.Remove(zoneName, out var zoneActor)) { return; }

        Logger.Information("Dropping instance zone {ZoneName} (owner {OwnerId})",
            Logger.Args(zoneName, _instanceOwnerId));

        _zoneEncounterIds.Remove(zoneName);
        _zoneAuthorityEpochs.Remove(zoneName);
        _retiringZones[zoneActor] = zoneName;
        Context.Watch(zoneActor);
        Context.Stop(zoneActor);
    }

    private bool TryEnsureManifest(TowerRunState run, out TowerEncounterManifest manifest) {
        manifest = null;
        if (run is null || run.Status != TowerRunStatus.Active) { return false; }
        if (run.CurrentManifest is not null) {
            manifest = run.CurrentManifest;
            return true;
        }

        manifest = run.BuildCurrentManifest();
        _towerRuns ??= new TowerRunCollection(PlayerDatabase.Instance.Store);
        if (_towerRuns.TrySave(run, run.AuthorityEpoch)) { return true; }

        // A failed commit must not leave an actor using a manifest that RavenDB does not own.
        run.CurrentManifest = null;
        manifest = null;
        Logger.Warning("Could not persist tower manifest for run {RunId} floor {Floor}.",
            Logger.Args(run.RunId, run.CurrentFloor));
        return false;
    }

    internal static bool IsTowerZoneStale(string loadedEncounterId, long loadedEpoch, string currentEncounterId, long currentEpoch)
        => !string.Equals(loadedEncounterId, currentEncounterId, StringComparison.Ordinal)
        || loadedEpoch != currentEpoch;

    private static string SanitizeZoneName(string zoneName)
        => zoneName.Replace('/', '-');

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
