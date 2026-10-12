using System;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// An AI controlled native player avatar. The server combat adapter reuses NPC statistics,
/// proximity admission and deck components; none of its NPC rendering schema reaches the client.
/// No account, session, persistent wizard or inventory is created for this participant.
/// </summary>
internal sealed class TowerWizardEntity(CoreObject active, CoreTemplate combatAdapter,
    CoreObjectInfo info, IActorRef zoneRef, Zone zone)
    : ZoneEntity(active, combatAdapter, info, zoneRef, zone) {
    private readonly long _floor = zone.TowerRunState.CurrentManifest?.Floor
        ?? Math.Max(1, zone.TowerRunState.HighestFloorCleared);
    private bool _isDazed;

    internal void MarkDefeated() {
        _isDazed = true;
        ZoneRef.Tell(new Imlight.CoreLib.Shared.Packets.ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = GetRetainedPresentationState(), Selfless = false,
        });
    }

    public override GAME_5_PROTOCOL.MSG_ENTERSTATE GetRetainedPresentationState() {
        if (!_isDazed) { return null; }
        var codec = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!codec.Serialize(new EmoteStateOverrideInfo {
            m_emoteName = "Dazed", m_loop = true, m_stateNameID = StringHash.Compute("Dazed"),
        }, 1, out var data)) {
            throw new InvalidOperationException("Cannot serialize native Tower wizard dazed state.");
        }
        return new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = ActiveGameObject.m_globalID, State = StringHash.Compute("Emoting"), Data = data,
        };
    }
    protected override void AutoAttachComponents() {
        foreach (var type in new[] { typeof(StatsComponent), typeof(NpcComponent),
            typeof(CombatCreatureDeckComponent), typeof(CombatCreatureAIComponent),
            typeof(AnimationComponent), typeof(RenderComponent) }) {
            AddComponent(type);
        }
        GetComponentOfType<NpcComponent>().NoTransfer = true;
    }

    public override WizClientObject GetClientBehaviorInstance() {
        var obj = CoreObjectFactory.InitializeCoreObjectBehaviors(new WizClientObject {
            m_globalID = ActiveGameObject.m_globalID,
            m_characterId = ActiveGameObject.m_globalID,
            m_debugName = ActiveGameObject.m_debugName,
            m_location = ActiveGameObject.m_location,
            m_orientation = ActiveGameObject.m_orientation,
            m_nMobileID = ActiveGameObject.m_nMobileID,
            m_permID = ActiveGameObject.m_permID,
            m_zoneTagID = ActiveGameObject.m_zoneTagID,
        }, 1);
        var profile = Zone.GetTowerEnemyProfile(ActiveGameObject.m_globalID)
            ?? throw new InvalidOperationException("AI wizard is missing its fenced Tower profile.");
        TowerWizardAvatar.Configure(obj, profile, _floor);
        obj.m_gameStats = GetComponentOfType<StatsComponent>().Stats.GetCombatGameStats();
        return obj;
    }
}
