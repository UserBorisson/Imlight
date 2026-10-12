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
                AcceptTowerRunState(message.ActivateRun);
            }
        }

        var transfer = message.Transfer is null ? null : TowerTransferPolicy.Copy(message.Transfer);
        if (transfer is null || transfer.OwnerCharId != _instanceOwnerId) {
            Sender.Tell(new TowerInstanceRouteResult { ErrorMessage = "Tower transfer owner does not match instance owner." });
            return;
        }

        if (TowerZonePaths.IsSanctuaryPath(transfer.DestinationZone)) {
            foreach (var combatRoom in _zones.Keys
                .Where(zonePath => TowerZonePaths.ShouldRetireCombatArenaOnSanctuaryTransfer(
                    zonePath, transfer.DestinationZone))
                .ToArray()) {
                DropInstanceZone(combatRoom);
            }
        }

        if (TowerZonePaths.TryGetRoomNumber(transfer.DestinationZone, out _)) {
            if (_towerRun is null || _towerRun.Status != TowerRunStatus.Active) {
                Sender.Tell(new TowerInstanceRouteResult {
                    Transfer = transfer,
                    ErrorMessage = "There is no active Golem Tower run for this instance.",
                });
                return;
            }

            if (_towerRun.CurrentFloor < 1) {
                Sender.Tell(new TowerInstanceRouteResult {
                    Transfer = transfer,
                    ErrorMessage = "The selected Golem Tower floor is invalid.",
                });
                return;
            }

            if (!TowerZonePaths.IsSanctuaryPath(transfer.DestinationZone)) {
                if (!_towerRun.IsFloorUnlocked(_towerRun.CurrentFloor)
                    || !TryEnsureManifest(_towerRun, out var manifest)
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

        if (TowerZonePolicy.IsEnabledTowerRoom(zoneName) && !TowerZonePaths.IsSanctuaryPath(zoneName)
            && !TowerZonePolicy.IsValidManifest(_towerRun, zoneName)) {
            Logger.Warning("Rejected Tower room {ZoneName}: no valid persisted owner manifest.", Logger.Args(zoneName));
            // No Zone actor is created; GameWorld's pending transfers receive the normal missing-zone error.
            return;
        }

        CreateAndLoadZone(zoneName, message);
    }

    private void CreateAndLoadZone(string zoneName, ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        if (TowerZonePolicy.IsEnabledTowerRoom(zoneName) && !TowerZonePaths.IsSanctuaryPath(zoneName)
            && !TowerZonePolicy.IsValidManifest(_towerRun, zoneName)) {
            Logger.Warning("Rejected invalid Tower room {ZoneName} at creation.", Logger.Args(zoneName));
            RejectTransfersWaitingForRetirement(zoneName);
            return;
        }
        var zoneActor = CreateZone(zoneName);
        _zones[zoneName] = zoneActor;
        if (TowerZonePolicy.IsEnabledTowerRoom(zoneName)) {
            // Managed Tower zones fail closed during initialization. Watch them so a failed actor
            // cannot remain cached and swallow every later transfer for this wizard.
            Context.Watch(zoneActor);
        }
        zoneActor.Tell(message);
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
            AcceptTowerRunState(message.State);
        }
    }

    internal static bool ShouldAcceptTowerRunState(TowerRunState current, TowerRunState incoming)
        => TowerRunState.IsAcceptableSuccessor(current, incoming);

    private void AcceptTowerRunState(TowerRunState state) {
        _towerRun = state.Copy();
        foreach (var zone in _zones.Values) {
            zone.Tell(new TowerRunChanged { State = _towerRun.Copy() });
        }
    }

    [MessageHandler(typeof(Terminated))]
    private void ReceiveZoneTerminated(Terminated message) {
        if (_retiringZones.Remove(message.ActorRef, out var zoneName)) {
            if (_loadsWaitingForRetirement.Remove(zoneName, out var load)) {
                CreateAndLoadZone(zoneName, load);
            }

            if (_transfersWaitingForRetirement.Remove(zoneName, out var transfers)
                && _zones.TryGetValue(zoneName, out var zone)) {
                foreach (var (transfer, originalSender) in transfers) {
                    zone.Tell(transfer, originalSender);
                }
            }
            return;
        }

        var terminatedZone = _zones.FirstOrDefault(pair => pair.Value.Equals(message.ActorRef));
        if (terminatedZone.Value is null || !TowerZonePolicy.IsEnabledTowerRoom(terminatedZone.Key)) {
            return;
        }

        if (_zones.TryGetValue(terminatedZone.Key, out var currentZone)
            && currentZone.Equals(message.ActorRef)) {
            _zones.Remove(terminatedZone.Key);
            _zoneEncounterIds.Remove(terminatedZone.Key);
            _zoneAuthorityEpochs.Remove(terminatedZone.Key);
            Logger.Warning("Evicted terminated Tower room actor for {ZoneName} (owner {OwnerId}).",
                Logger.Args(terminatedZone.Key, _instanceOwnerId));
        }
    }

    private void RejectTransfersWaitingForRetirement(string zoneName) {
        if (!_transfersWaitingForRetirement.Remove(zoneName, out var transfers)) { return; }
        foreach (var (_, originalSender) in transfers) {
            originalSender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorCode = 1,
                ErrorMessage = "The Tower room cannot load without a valid run and manifest.",
            });
        }
    }

    private IActorRef CreateZone(string zoneName) {
        var zoneActorName = SanitizeZoneName(zoneName);
        var zoneId = GetNextDynamicZoneId();
        TowerRunState zoneRun = null;
        if (_towerRun?.Status == TowerRunStatus.Active
            && TowerZonePaths.TryGetRoomNumber(zoneName, out var roomNumber)) {
            if (roomNumber == TowerRunState.SanctuaryRoomNumber) {
                zoneRun = _towerRun;
            }
            else if (TryEnsureManifest(_towerRun, out var manifest) && manifest.PhysicalRoomNumber == roomNumber) {
                zoneRun = _towerRun;
                _zoneEncounterIds[zoneName] = manifest.EncounterId;
                _zoneAuthorityEpochs[zoneName] = zoneRun.AuthorityEpoch;
                Logger.Information("Golem Tower run {RunId} creates floor {Floor}, attempt generation {AttemptGeneration}, encounter {EncounterId} in {ZoneName}.",
                    Logger.Args(zoneRun.RunId, zoneRun.CurrentFloor, zoneRun.AttemptGeneration, manifest.EncounterId, zoneName));
            }
        }
        if (TowerZonePolicy.IsEnabledTowerRoom(zoneName) && !TowerZonePaths.IsSanctuaryPath(zoneName)
            && !TowerZonePolicy.IsValidManifest(zoneRun, zoneName)) {
            throw new InvalidOperationException("A Tower room cannot be created without a valid manifest.");
        }
        var zoneSnapshot = zoneRun?.Copy();
        var zoneProps = _zonePropsFactory?.Invoke(zoneName, zoneId, zoneSnapshot)
            ?? Zone.Core.Zone.Props(zoneName, zoneId, zoneSnapshot);
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
