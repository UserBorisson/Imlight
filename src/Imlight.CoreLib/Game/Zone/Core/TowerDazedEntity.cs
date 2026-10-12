using System;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>A visible, non-combat replica of the NPC defeated in the Tower encounter.</summary>
internal sealed class TowerDazedEntity(
    CoreObject activeGameObject,
    CoreTemplate template,
    CoreObjectInfo info,
    Akka.Actor.IActorRef zoneRef,
    Zone zone)
    : ZoneEntity(activeGameObject, template, info, zoneRef, zone) {

    internal static bool AllowsPresentationComponent(System.Type componentType)
        => componentType == typeof(AnimationComponent) || componentType == typeof(RenderComponent);

    protected override void AutoAttachComponents() {
        foreach (var (componentType, shouldAttachMethod) in ZoneEntityComponentRegistry.GetRegisteredComponents()) {
            if (!AllowsPresentationComponent(componentType)) { continue; }

            try {
                if ((bool)shouldAttachMethod.Invoke(null, [Template])) {
                    AddComponent(componentType);
                }
            }
            catch (Exception ex) {
                Logger.Warning("Display-only Tower entity could not attach {0}: {1}",
                    Logger.Args(componentType.Name, (ex.InnerException ?? ex).Message));
            }
        }
    }

    [MessageHandler(typeof(TowerDazedEnterStateMessage))]
    private void ReceiveTowerDazedEnterState(TowerDazedEnterStateMessage message) {
        ChangeState("Emoting", new EmoteStateOverrideInfo {
            m_emoteName = "Dazed",
            m_loop = true,
            m_stateNameID = StringHash.Compute("Dazed"),
        });
    }
}
