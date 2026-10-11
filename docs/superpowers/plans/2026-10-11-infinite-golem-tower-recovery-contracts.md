# Infinite Golem Tower Recovery Contracts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enforce a single replay allowance for the Adventure run, fence stale server processes from Tower resource/reward writes, and charge recovery only for a persisted in-combat interruption.

**Architecture:** Use a RavenDB compare-exchange process lease with heartbeat and expiry. Every Tower mutation will validate and touch that lease inside the same cluster-wide transaction that writes the run, wizard resources, or reward receipt; a stale process therefore cannot pass a separate preflight check and then write afterward. Persist an explicit encounter lifecycle (`NotStarted`, `InCombat`, `RecoveryPending`) so a saved manifest alone does not imply an interrupted combat.

**Tech Stack:** C# 14, Akka.NET actors, RavenDB.Client 7.2.4, local RavenDB integration modes.

**Spec:** `C:\Users\Nikola\.codex\attachments\648b2aa4-8395-4f5f-bac6-7fc71f0f5c7f\Pasted text.txt`; kickoff `C:\Users\Nikola\.codex\attachments\d04d9877-3f51-4192-9364-f8bef293d1c9\Pasted text.txt`. The review supplies the v1.2.1 rule of one replay per Adventure run; kickoff keeps Tower disabled by default and requires ordinary combat to remain unchanged.

## Global Constraints

- Keep `Tower.Enabled = false` by default.
- Preserve normal duel resource, flee, reward, and disconnect behavior outside Tower encounters.
- Commit Tower run mutations and authorized side effects with RavenDB concurrency/fencing checks.
- Allow one recovery replay for the entire Adventure run; do not replenish it on floor clear or checkpoint restart.
- A process takeover must first own the shared Tower server lease; the lease check and every Tower durable side effect must share an atomic transaction boundary.

## Review Focus

- Two server processes contend for the owner lease; one wins and the stale process cannot save run state, character HP/mana, or rewards.
- A manifest persisted before combat can be resumed after process restart without consuming the replay.
- A delayed snapshot from an older run cannot replace a newer container state, including when the current run is terminal.
- Starting combat persists `InCombat` before gameplay actions can mutate resources.
- The first technical interruption consumes the single run replay; clearing floors and restarting at checkpoints do not replenish it.
- A later interruption after the allowance is spent fails closed into checkpoint defeat/restart without native flee or Tower rewards.

---

### Task 1: Exclusive Tower server ownership

**Files:**
- Create: `src/Imlight.CoreLib/Game/Tower/TowerServerOwnership.cs`
- Modify: `src/Imlight.CoreLib/Game/World/GameWorld.cs`
- Modify: `src/Imlight.CoreLib/Game/Tower/TowerRunCollection.cs`
- Test: `tests/Imlight.Customization.Tests/TowerPersistenceTests.cs`

**Interfaces:**
- `TowerServerOwnership.TryAcquire()`, `Renew()`, `Release()`, and `Touch(IDocumentSession)` use a process incarnation, lease ID, 5-second heartbeat, and 20-second expiry.
- The process lease is stored at one stable compare-exchange key. The lease is touched within each cluster-wide mutation transaction.
- `GameWorld` acquires/registers the process lease before accepting Tower routes, renews it periodically, rejects Tower transfers after lease loss, and releases it when the last world actor stops.

- [x] Write failing Raven integration tests for exclusive acquisition, blocked live-owner takeover, expiry takeover, and old-owner transaction rejection.
- [x] Run `--tower-persistence` and confirm the new ownership assertions fail first.
- [x] Implement compare-exchange ownership and actor heartbeat.
- [x] Run `--tower-persistence` and confirm the lease tests pass, including an already-open stale cluster transaction.

### Task 2: Run-wide recovery and explicit duel lifecycle

**Files:**
- Modify: `src/Imlight.CoreLib/Game/Tower/TowerRunState.cs`
- Modify: `src/Imlight.CoreLib/Game/Tower/TowerRunCollection.cs`
- Modify: `src/Imlight.CoreLib/Game/Zone/Components/CombatDuelComponent.cs`
- Test: `tests/Imlight.Customization.Tests/TowerRunTests.cs`
- Test: `tests/Imlight.Customization.Tests/TowerPersistenceTests.cs`
- Test: `tests/Imlight.Customization.Tests/TowerDuelLifecycleTests.cs`

**Interfaces:**
- Persist `TowerEncounterLifecycle` with `NotStarted`, `InCombat`, and `RecoveryPending` values.
- Add an idempotent `TryBeginEncounter` transition and make recovery consume a single `RecoveryAllowanceRemaining` value for the run.
- Entering `InCombat` requires the persisted complete admitted roster. Process takeover consumes replay only from `InCombat`; exhausted in-combat takeover records defeat and resumes from checkpoint.
- Floor victory and `RestartFromCheckpoint` preserve the remaining run-wide allowance.

- [x] Write failing tests for lifecycle transitions, safe manifest takeover, one replay across multiple floors/checkpoint restart, and exhausted allowance.
- [x] Run the focused tower tests and confirm they fail for the current per-floor reset and manifest-based takeover.
- [x] Implement durable lifecycle/replay transitions and component start/abort wiring.
- [x] Run `--tower-run`, `--tower-persistence`, and `--tower-duel` and confirm they pass.

### Task 3: Fence character resource and reward writes

**Files:**
- Modify: `src/Imlight.CoreLib/Game/Tower/TowerRunCollection.cs`
- Modify: `src/Imlight.CoreLib/Game/Tower/TowerAwardCollection.cs`
- Modify: `src/Imlight.CoreLib/Game/Combat/CombatDuelSubCircle.cs`
- Modify: `src/Imlight.CoreLib/Game/Zone/Components/CombatDuelComponent.cs`
- Modify: `src/Imlight.CoreLib/Game/Services/CombatService.cs`
- Test: `tests/Imlight.Customization.Tests/TowerPersistenceTests.cs`
- Test: `tests/Imlight.Customization.Tests/TowerAwardTests.cs`

**Interfaces:**
- Add a `TryUpdateCombatResources` transaction that validates the current process lease, run epoch/revision/lease, encounter ID, and `InCombat` lifecycle while updating run revision and the wizard's persisted HP/mana atomically.
- Route Tower spell damage, healing, mana deductions, intentional flee mana drain, and recovery restore through that transaction; ordinary duels continue calling the existing Wizard update methods.
- `TowerAwardCollection.Commit` validates its request against the durable pending-award outbox and touches the current process lease in the same cluster-wide transaction as XP, inventory, progression, and receipt writes.

- [x] Write failing tests proving a stale process cannot update HP/mana or create/commit a reward after takeover.
- [x] Run `--tower-persistence` and `--tower-awards` and confirm the stale write assertions fail before fencing is implemented.
- [x] Implement atomic fenced resource and reward mutations.
- [x] Run `--tower-persistence`, `--tower-awards`, and ordinary combat regression modes.

### Task 4: Full verification and deployment notes

**Files:**
- Modify: `docs/InfiniteGolemTower-LocalTest.md`
- Modify: `docs/superpowers/plans/2026-10-11-infinite-golem-tower-review-fixes.md`

- [x] Document the one-per-run replay rule, lease expiry/restart behavior, safe-manifest takeover, and exhausted-replay checkpoint behavior.
- [x] Run the Release solution build and every tower mode plus default loopback mode against the local RavenDB endpoint.
- [x] Inspect staged diff, commit the follow-up, push `feature/infinite-golem-tower` to the existing `tower` remote, and verify remote SHA and clean worktree.
