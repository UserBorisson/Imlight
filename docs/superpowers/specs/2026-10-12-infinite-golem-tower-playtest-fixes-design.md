# Infinite Golem Tower Playtest Fixes

## Goal

Make multi-enemy encounters start reliably, keep a wizard's mana stable across Tower cleanup and floor transitions, and preserve floor progress so the next uncleared floor is the retry point. After a victory, the wizard remains in the cleared room and uses the existing Tower route to continue.

Scope is limited to enabled Infinite Golem Tower rooms and their run state. Shared Golem Court, unrelated zones, ordinary combat, rewards, encounter authority fencing, and the existing feature toggle retain their current behavior.

## Current failure causes

- `TowerEnemySpawnPlanner` selects an authored spawn anchor independently for each manifest slot, applies offsets, and does not check the final positions as a group. Different authored anchors can cancel those offsets, leaving enemies on top of each other.
- `TowerRunState.ApplyResolution` records `HighestFloorCleared`, but only floors 5 and 10 update `CheckpointFloor`. `RestartFromCheckpoint` therefore sends a defeated or extracted run back to floor 1 or 6 even after later floors were cleared.
- `CombatDuelComponent.IsTowerEncounter` is computed from mutable zone run state. After a victory advances the run and clears its active manifest, a late flee/disconnect can be handled by the ordinary flee path. That path sets mana to zero. Development logs show a flee arriving after `PlayerWin` for the same duel.
- Clear feedback is constructed separately from the transition state and has a special floor-5 instruction. The message should be derived from committed progress and clearly identify the next playable floor.

## Design

### Encounter placement

Build all manifest enemy placements as one deterministic plan. Compare final horizontal positions, not only authored anchors. When candidate anchors produce positions closer than the minimum separation, choose another valid authored anchor/offset deterministically. Translate each enemy's path nodes using the final placement offset so movement starts at the spawned location. If the room cannot provide a complete, separated roster, fail closed during room initialization; do not start a broken encounter or fall back to legacy enemies.

The minimum separation will be a named Tower planner constant and covered by a pairwise-distance regression assertion. Existing one- and two-enemy manifest limits remain unchanged.

### Combat and mana lifecycle

Bind each duel instance to the Tower encounter identity and Tower status when it is initialized. Use this stable identity to classify later participant interruptions, even if a victory has already advanced the mutable run state. Make duel completion idempotent so late cleanup cannot resolve the same encounter again. A stale flee/disconnect after Tower victory or technical cleanup must not enter ordinary flee handling or zero mana. A deliberate player flee during a live Tower encounter retains the existing ordinary flee mana cost.

### Progress and feedback

Treat the furthest durably cleared floor as the checkpoint. On defeat or extraction, restart the run at `min(HighestFloorCleared + 1, FloorCount)`; a new run with no clears begins at floor 1. Floor victories continue to advance only after an authoritative combat resolution is saved.

Generate feedback from the committed run state: identify the floor just cleared, progress out of 10, and the next floor. Keep the existing floor-5 Court exit/re-entry route for entering floor 6, and the existing completion behavior after floor 10. No automatic transfer occurs after ordinary floor victories.

## State and compatibility

No new persistence document or client protocol is required. `HighestFloorCleared` is already stored in `TowerRuns`; the implementation changes restart selection to use it. Existing `CheckpointFloor` may remain for compatibility with serialized run documents, but it no longer limits retry progress. Existing duel and transfer messages remain the communication mechanism.

The one-replay-per-run interruption allowance, resource recovery snapshots, idempotent awards, authority fencing, private instance ownership, and fail-closed Tower room loading remain in force.

## Verification

Focused regression coverage will prove:

- Every generated multi-enemy roster has distinct final positions separated by the named minimum, and path start nodes match those positions.
- An unavailable safe placement rejects room construction instead of admitting a partial encounter.
- Late flee/disconnect cleanup after a committed Tower win does not drain mana or resolve the encounter again; a deliberate flee during an active Tower encounter retains existing behavior.
- Defeat and extraction after clears at floors 1, 3, 5, 8, and 9 resume at the next uncleared floor; an uncleared floor is retried directly.
- Clear feedback reports committed floor progress and the correct next floor, including the floor-5 route and floor-10 completion.
- Existing Tower, ordinary combat, and unrelated regression checks remain unchanged.

The change is ready for deployment only after the focused and existing Tower regression modes and the Release build pass. In-game playtesting must confirm that multi-enemy fights admit the complete roster, mana remains stable through victory/transition, progress survives extraction/re-entry, and the player still advances manually after a clear.
