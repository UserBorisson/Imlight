# Infinite Golem Tower Replacement Design

**Status:** Approved with refinements from the 2026-10-11 design review
**Base:** feature/infinite-golem-tower at 7eda917991f42d21b9763c5fa51e7c94b609e8bc
**Scope:** Replace runtime Golem Tower gameplay when Tower mode is enabled, using the existing five rooms and server-side Tower implementation.

## Goal

Make the existing Golem Tower entrance and five physical rooms serve only the Infinite Golem Tower Adventure mode while `Tower.Enabled = true`. The Director owns run initialization, private instance routing, logical floors 1 through 10, encounters, outcomes, checkpoints, recovery, and durable rewards. Preserve the shared Court, original visual assets, standard Wizard101 combat calculations, and behavior outside Tower.

## Requirements and constraints

- Intercept only the five exact physical Tower-room paths, even when the client sets `IsPrivate = false`; keep the Court public.
- Resolve the Tower instance owner from the authenticated wizard in `ZoneService`, not from a transfer's `OwnerCharId`. Reject a mismatch between the authenticated wizard and any supplied owner value.
- Create or resume one authoritative RavenDB run for the authenticated wizard and route that wizard into an owner-only Tower instance.
- Return the Director-resolved physical room as explicit route data and use it to queue the client transfer and saved destination. Do not rely on mutation of the request actor message to propagate the route.
- Keep Golem Court shared. Preserve its existing portal as the entry request into the Tower.
- When Tower mode is enabled, reject entry if lease acquisition, run creation/resumption, manifest persistence, or private routing fails. Never continue into legacy Tower gameplay after such a failure.
- The run manifest is authoritative for enemy roster and encounter identity. The Director controls floor transitions, room recycling, validated victory and defeat, checkpoint extraction, and rewards.
- Suppress legacy Tower enemy spawning, room advancement, encounter triggers, and native rewards in Tower mode. Reuse the existing five rooms, NPC models, spells, and animations as client-facing assets. Keep ordinary Wizard101 combat rules.
- Fail closed in `InstanceContainer` and Tower room construction when run state or manifest data is missing or invalid. Reject entry if the complete manifest roster cannot be instantiated.
- Preserve the `Tower.Enabled` switch. Disabled mode is an explicit rollback to the original Tower. Enabled mode is fail-closed and has no legacy fallback.
- Preserve the v1.2.1 safety contract: complete enemy admission and combat-death evidence; technical abort for incomplete, stale, invalid, or interrupted encounters; one recovery replay per Adventure run, charged only after combat begins; floor-entry HP/mana recovery; exclusive process ownership and fencing of run, resource, and reward writes; idempotent awards.
- Keep Tower-specific behavior gated by the Tower flag and exact Tower zone paths. Leave unrelated zones, quests, instances, and ordinary combat behavior unchanged. Legacy Tower quest progression may break.
- Build and test the full Release solution, commit and push the implementation to `feature/infinite-golem-tower`, and deploy only to the authorized isolated development server. Preserve the original working environment.

## Current findings

The approved feature branch is clean at the base commit above, and the `tower` remote is configured for `UserBorisson/Imlight`. The branch already contains Tower run persistence, run-wide recovery state, compare-exchange ownership fencing, reward transactions, encounter admission checks, and Tower-specific combat handling.

The captured playtest trace shows a non-private transfer into `WC_Golem_Tower_1`. Current `GameWorld.ReceiveZoneTransfer` only creates or resumes a Tower run when `IsPrivate` is true. The room is consequently loaded through the normal instance path without a persisted Tower run. `ZoneObjectSupervisor` then has no manifest and may initialize authored legacy enemies. It currently logs a warning if the authored usable duelist objects do not cover the whole manifest roster. `ZonePathSupervisor` still attaches authored roaming spawners, and `ZoneTriggerSupervisor` still installs the original room teleport results.

The installed transfer data confirms that the shared Court portal targets physical room 1 and that the rooms contain legacy transitions between Tower rooms. Those transfers must become requests interpreted by the Director. Successful combat progression already has a Tower-specific transfer message and should remain the source of logical floor changes.

The client-facing transfer preparation currently uses the original request in `ZoneService`, while the Tower route mutates a request message inside the instance actor. The replacement must return the resolved destination explicitly through its internal routing result so checkpoint resumes identify the correct physical room to the client.

## Chosen architecture

Extend the existing authoritative zone and instance pipeline with a Tower-only policy. Do not build a second zone engine. Apply the policy only when the feature flag is enabled and the destination is one of the five exact physical Tower room paths. Resolve and bind the owner to the authenticated active wizard before the request reaches `GameWorld`.

### Entry and ownership

Before public/private routing, `GameWorld` recognizes every physical Tower-room destination independently of the client's `IsPrivate` bit. `ZoneService` binds the request to the authenticated wizard; a conflicting supplied owner is rejected. `GameWorld` acquires or validates the shared Tower server lease, creates or resumes that wizard's run, ensures the current manifest is durably saved, marks the transfer private, and creates or reuses that wizard's `InstanceContainer`. The container routes the transfer using the manifest's current logical floor and the existing five-room mapping.

The Court remains a shared public zone. Its existing portal continues to request entry to room 1; the server interprets the resulting room transfer as Tower entry. Direct room transfers, relogs, and retries follow the same run-resume path without resetting active progression.

The instance route returns an explicit resolved room path and transfer location. `GameWorld` passes a new routed transfer to the selected room, and an internal route result carries the resolved destination back to `ZoneService`. `ZoneService` uses that result for the client's zone-transfer request and queued character destination. The wire shape of the client-facing zone transfer response remains unchanged. A checkpoint resume that arrives requesting room 1 but resolves to another physical room must name the resolved room to the client.

If any run or authority step fails, `GameWorld` returns an error before loading or forwarding the room transfer. `InstanceContainer` rejects missing or invalid run/manifest state. Tower room construction validates the run, manifest, and exact physical path before starting Tower supervisors. If any required enemy in the complete manifest roster cannot be instantiated and registered, zone construction fails and pending transfers receive an error. There is no public-zone or legacy-enemy fallback while Tower mode is enabled.

### Tower room policy

Tower mode is carried into Tower room construction so each relevant supervisor can distinguish a valid manifest-backed Tower room from a normal zone.

- `ZoneObjectSupervisor` creates the manifest's combat roster using the existing authored NPC presentation assets and available room positions. It suppresses extra legacy duelist objects, verifies every manifest slot was instantiated and registered, and fails Tower zone construction if any slot is missing. It creates no legacy combatants when Tower state or manifest data is missing.
- `ZonePathSupervisor` preserves room paths and nodes but does not activate authored roaming creature spawners in Tower rooms.
- `ZoneTriggerSupervisor` disables legacy Tower combat-spawn results and room-to-room progression teleports. The Court entry portal remains usable. An explicit Tower-controlled exit/extraction route remains available so legacy suppression cannot trap a player in the instance; exits use the existing Tower checkpoint path.
- Existing Tower combat handling remains the only route to encounter resolution and native reward suppression. `InstanceContainer`, `Zone`, and Tower room supervisors reject enabled Tower rooms without valid run/manifest state; those rooms cannot be treated as ordinary zones or ordinary combat.

The existing zone art, NPC models, spells, animations, and combat engine remain in use. Tower-specific profiles and manifests determine the generated enemies. No Tower policy is applied to other zones.

### Progression, rewards, and recovery

The persisted `TowerRunState` remains the source of truth for the logical floor, manifest, checkpoint, recovery allowance, encounter lifecycle, authority epoch, and pending awards. The five physical rooms are selected from that state, so logical floors 6 through 10 recycle rooms 1 through 5 only after the previous room actor has retired.

Only a fully admitted manifest roster with server-recorded combat-death evidence can clear a floor. Technical aborts do not grant victory, defeat, XP, or loot. The one run-wide recovery replay is spent only for an interruption after the durable lifecycle entered combat. Process takeover must own the compare-exchange lease, and every durable Tower mutation remains fenced in its RavenDB transaction. Tower awards remain idempotent and native Tower rewards stay suppressed.

Leaving the Tower uses the Director's checkpoint/extraction rules. Floor completion uses the existing Tower transfer message and cannot be initiated by an authored legacy room teleport. Ordinary combat outside the Tower keeps its existing behavior.

## Alternatives considered

1. **Tower policy in the existing zone pipeline — selected.** Reuses existing room, actor, combat, and transfer services. It requires targeted suppression in the existing object, path, and trigger supervisors and keeps normal zones on their existing behavior.
2. **A separate Tower zone engine.** Gives the Tower a stronger implementation boundary but duplicates zone loading, entity supervision, and transfer behavior, increasing maintenance and ordinary-zone regression risk.

## Implementation areas

- `Game/Services/ZoneService.cs`, `Game/World/GameWorld.cs`, `Game/World/InstanceContainer.cs`, and `Game/Tower/TowerZonePaths.cs` for authenticated owner binding, explicit resolved-destination routing, fail-closed entry, run activation, and room ownership.
- `Game/Zone/Core/Zone.cs` and the object, path, and trigger supervisors for Tower-mode policy, manifest roster validation, and legacy suppression.
- Existing Tower run, combat, service, and award code only where needed to enforce Director-owned transitions, safe extraction, and the v1.2.1 invariants.
- Tower-focused regression tests for non-private entry, resume destination, roster construction, simultaneous private loading, and fail-closed behavior; ordinary combat regressions; deployment notes; and isolated development deployment scripts only where necessary.

## Acceptance and verification

- Entering from the shared Court with `IsPrivate = false` creates exactly one persisted `TowerRuns` document and enters a private owner instance for the authenticated wizard.
- Relaunching or re-entering resumes active progression and cannot reset the run.
- A checkpoint resume sends the Director-resolved physical room to the client, even when the requested entry path names another room.
- Two owners cannot observe or reuse each other's Tower room actor or manifest.
- Two owners remain isolated during simultaneous room loading.
- A missing lease, run, or manifest rejects Tower entry before any legacy room gameplay can load.
- Failure to instantiate any manifest enemy rejects Tower room construction; it never falls back to authored legacy enemies.
- Original roaming enemies and room-progression teleports never activate in enabled Tower mode, and explicit checkpoint extraction remains usable.
- Successful and failed encounters follow the persisted Director state; floors 1 through 10 advance through five physical rooms, with floors 6 through 10 recycling the actors safely.
- Tower native XP and loot are suppressed while the configured durable Tower award is committed once. Ordinary-zone rewards remain unchanged.
- Recovery, takeover fencing, admission, outcome evidence, and stale-write cases satisfy the v1.2.1 contract.
- Run focused new regression cases, all existing Tower test modes, all existing regression modes, and the complete Release solution build.
- Before deployment, preserve a fresh development backup. Deploy only to the isolated development environment with Tower explicitly enabled. Verify that entry creates a persisted run and loads the generated floor 1 encounter. Do not perform intentional crash testing as part of this playtest.
- In the client playthrough, verify that entering the Court portal starts floor 1 and the first validated victory advances to floor 2.

## Rollback

Set `Tower.Enabled = false` in the isolated development configuration and restart the development server to select the original Tower behavior. With the flag enabled, errors reject Tower entry and never load legacy Tower gameplay.
