# Infinite Golem Tower review fixes

## Goal

Correct the four high-priority lifecycle and persistence gaps identified in the review on `feature/infinite-golem-tower`, and add behavior tests that run through the same admission, resolution, interruption, and recovery components used by the live duel flow.

## Decisions

- Tower disconnects and logout interruptions abort the current duel without native flee, mana drain, victory, defeat, XP, or loot. Ordinary duels keep their current flee behavior.
- A tower floor clear requires every manifest enemy slot to be admitted and each slot to have server-recorded combat-death evidence. Aggregate counts are not sufficient.
- Any stale, invalid, incomplete, or nonterminal resolution takes a technical-abort path. Only validated Victory and Defeat mutate tower progress.
- Persist a per-floor-entry HP/mana snapshot for admitted wizards, one recovery replay per Adventure run, and a process-incarnation/authority-lease record. A new process must acquire the shared RavenDB compare-exchange lease before it can write Tower data. Every run, character-resource, and reward mutation touches that lease in the same cluster-wide transaction.
- Rotate the run authority after a safe-boundary takeover without spending the encounter replay. Charge the replay only when the persisted lifecycle says combat actually began. If a later active interruption occurs after the replay is spent, fail closed to checkpoint defeat/restart without Tower rewards.
- The review identifies one replay per Adventure run as the v1.2.1 contract. Floor clears and checkpoint restarts preserve the remaining run-wide allowance.

## Implementation steps

1. Add failing tests for complete enemy admission, duplicate/missing slots, combat-death evidence, technical outcome classification, disconnect interruption versus intentional flee, floor-entry resources, recovery allowance, process incarnation, and takeover fencing.
2. Add a tower duel roster/lifecycle component and wire actual `CombatDuelComponent` admission, death, disconnect, logout, flee, resolution, and abort paths through it.
3. Extend durable run state and RavenDB collection operations with floor-entry resources, the run-wide recovery allowance, explicit encounter lifecycle, process incarnation, and compare-exchange lease fencing. Restore recovery resources through a transaction that also validates the run and lease.
4. Keep normal combat semantics unchanged. A first technical abort can replay once; an exhausted active interruption moves to checkpoint restart without Tower rewards.
5. Run focused tower tests, the complete existing tower test modes, and the Release build; inspect the final diff and push the follow-up commit to the existing `tower` remote.

## Validation

- Confirm each new regression fails before its implementation and passes after it.
- Run the tower state, persistence, actor, award, and default loopback modes against the configured local RavenDB test endpoint.
- Build the solution in Release configuration.
- Leave actual Wizard101 client smoke testing pending because it requires the user's local game client and disposable wizard.
