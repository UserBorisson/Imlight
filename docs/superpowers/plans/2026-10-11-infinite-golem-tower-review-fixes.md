# Infinite Golem Tower review fixes

## Goal

Correct the four high-priority lifecycle and persistence gaps identified in the review on `feature/infinite-golem-tower`, and add behavior tests that run through the same admission, resolution, interruption, and recovery components used by the live duel flow.

## Decisions

- Tower disconnects and logout interruptions abort the current duel without native flee, mana drain, victory, defeat, XP, or loot. Ordinary duels keep their current flee behavior.
- A tower floor clear requires every manifest enemy slot to be admitted and each slot to have server-recorded combat-death evidence. Aggregate counts are not sufficient.
- Any stale, invalid, incomplete, or nonterminal resolution takes a technical-abort path. Only validated Victory and Defeat mutate tower progress.
- Persist a per-floor-entry HP/mana snapshot for admitted wizards, one recovery allowance per floor attempt, and a process-incarnation/authority-lease record. A new process can recover only through an optimistic-concurrency takeover transaction that records the prior fence and consumes the allowance. Restore saved resources on recovery. Same-process re-entry does not consume the crash allowance.
- Rotate the process fence after a restart at a safe floor boundary without spending the encounter recovery allowance; a live or already recovering encounter still requires the allowance.
- The kickoff specifies crash fencing and interruption recovery but not numeric limits. One recovery per floor attempt is the conservative explicit policy for this patch.

## Implementation steps

1. Add failing tests for complete enemy admission, duplicate/missing slots, combat-death evidence, technical outcome classification, disconnect interruption versus intentional flee, floor-entry resources, recovery allowance, process incarnation, and takeover fencing.
2. Add a tower duel roster/lifecycle component and wire actual `CombatDuelComponent` admission, death, disconnect, logout, flee, resolution, and abort paths through it.
3. Extend durable run state and RavenDB collection operations with floor-entry resources, per-attempt recovery allowance, process incarnation, and a verifiable compare-and-swap takeover record. Restore recovery resources through the existing session/service path.
4. Keep normal combat semantics unchanged and ensure tower technical aborts produce no combat result or reward and leave the floor available for recovery.
5. Run focused tower tests, the complete existing tower test modes, and the Release build; inspect the final diff and push the follow-up commit to the existing `tower` remote.

## Validation

- Confirm each new regression fails before its implementation and passes after it.
- Run the tower state, persistence, actor, award, and default loopback modes against the configured local RavenDB test endpoint.
- Build the solution in Release configuration.
- Leave actual Wizard101 client smoke testing pending because it requires the user's local game client and disposable wizard.
