# Tower Progression and Native Economy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the confirmed combat controls, readable wizard labels, lower enemy health, reviewed spell progression, native Blue Arena Ticket rewards and functional Tome equipment progression.

**Architecture:** Preserve the current player-avatar combat adapter and Raven fencing. Add explicit content/policy units for spell eligibility, native wallet operations, compact clear history and per-item stat targets; wire them into existing duel, award, player and Tome flows. Keep gameplay changes in the existing isolated Tower worktree and deploy only verified checkpoints.

**Tech Stack:** C#/.NET 10, Akka actors, RavenDB, installed native Wizard101 object/protocol schema, existing console regression harness, PowerShell deployment scripts.

**Spec:** `docs/superpowers/specs/2026-10-13-tower-progression-and-economy-design.md`.

## Global Constraints

- Preserve unrelated work, saves, native balances, existing learned spells, Training Points and item identity.
- Gray/disable casting and targeting during enemy planning while allowing persistent discard and Treasure Card drawing. Retain existing native pip/effect ordering and 2–7 second enemy planning.
- Normal level = min(floor, native cap); nameplate level = logical floor. Normal stats/rewards stop growing at the cap.
- HP factors: level 1=.85, 10=.90, 30=1.00, 50=1.05, 100=1.10, cap=1.15, interpolated.
- Early utilities: floor-3 blade, floor-5 trap, floor-8 support; enemies first access them on floors 4/6/9 respectively.
- Reward band B = 1 + floor((min(floor, cap)-1)/10); repeat=3B, first-clear total=10B; upgrade tier B costs 10B.
- Item target levels 1/10/20/.../cap, gated by character level L and permanent clear L−1. No charge for ineffective tiers.
- Upgrade eight equipped combat slots only; persistent gap-fill ordinary stats, immutable upgrade school, no special card/socket/pip upgrades or refunds.
- No custom Ticket charges for spells. Primary-school grants automatic, secondary-school selection free with level/spell prerequisites; learned spells permanent and usable everywhere.
- Client acceptance belongs to the user; never infer it from source tests or server readiness.

## Review Focus

- Stale/reordered hand and duel updates must not re-enable casting during enemy planning or block discard/Draw.
- Exact spell variants with artificially low metadata must never bypass the approved ID catalog.
- Concurrent native spending and migration/award retries must neither lose currency nor double-credit it.
- Equip/relog/repeated stat recalculation must not stack upgrade bonuses or alter jewels/native special features.
- Reset and sparse historical clears must preserve first-clear eligibility exactly; highest floor alone is insufficient.

## Task 1: Native schema and content audit

**Files:** create `docs/superpowers/specs/2026-10-13-tower-native-content-audit.md`; inspect `CombatDuelComponent.cs`, `TowerWizardAvatar.cs`, `TowerWizardEntity.cs`, `TowerWizardDeckModel.cs`, `TowerTomeComponent.cs`, `ServerWizGameStats.cs`, `TrainService.cs` and installed Root.wad/protocol definitions.

**Produces:** documented native hand/turn permission fields, nameplate fields and limits, Blue Arena Ticket field/cap/writers, exact spell catalog and school/slot numeric target tables. These discoveries must precede dependent production edits.

- [ ] Trace current planning messages and native alternating-turn client state; identify the native disabled-card/target mechanism while preserving hand-management requests. Record payload fields and ordering; avoid client patches without a separately approved need.
- [ ] Trace native player name rendering and numeric field limits. Establish a native nameplate payload supporting school and logical floor, including floors beyond byte level range, without mutating authoritative native level.
- [ ] Identify Blue Arena Tickets from installed schema/native UI use rather than assuming `m_currentEventCurrency1`, `m_currentPvPCurrency` or Arena points are equivalent. Enumerate every writer and native capacity behavior.
- [ ] Resolve exact learned-spell IDs and approved normal levels for seven schools, including quest spells and secondary prerequisites. Audit mass/item/Treasure variants and unsupported native effects. Record the floor-8 school-specific support choice explicitly.
- [ ] Record slot/school tier targets using installed equipment/base stats and inspect legacy paid-upgrade records read-only. Propose a value-preserving legacy conversion mapping.
- [ ] Review any unsupported native transport, wallet capacity or materially different legacy-migration finding with the user before dependent implementation. Present exact spell/stat tables for review; no placeholders authorize unreviewed stronger variants.
- [ ] Commit only the audit/design artifacts, preserving unrelated work.

## Task 2: Planning controls, labels and health

**Files:** modify `src/Imlight.CoreLib/Game/Zone/Components/CombatDuelComponent.cs`, `src/Imlight.CoreLib/Game/Tower/TowerCombatTurnPolicy.cs`, `TowerWizardAvatar.cs`, `TowerWizardStatModel.cs`, `TowerRunState.cs` and the native player adapter only as required by Task 1. Tests: `TowerDuelLifecycleTests.cs`, `TowerNativeWizardTests.cs`, `TowerWizardStatsTests.cs`.

**Interfaces:** add `TowerWizardStatModel.GetNormalHealthFactor(int level)`; retain public avatar/configuration and existing authoritative phase checks.

- [ ] Add failing tests for native enemy-planning lock payload, player-phase unlock, late hand updates, discard/Draw persistence and cast/target rejection. Test native nameplate payload on floors 1/cap/10000 while native effective level stays capped.
- [ ] Add HP anchor/interpolation/cap tests for all seven native school bases; assert equipment health is not applied twice and Normal no longer grows beyond cap.
- [ ] Build harness and run affected modes to observe failures before changing production behavior.
- [ ] Implement audited native phase/hand flags and payload order, native nameplate display, confirmed HP curve and capped Normal health. Keep Scaled Challenge separate and ordinary game behavior unchanged.
- [ ] Run `--tower-duel`, `--tower-native-wizard`, `--tower-run`, `--tower-zone-policy` and ordinary loopback. Commit the coherent combat checkpoint after passing evidence.

## Task 3: Explicit spell catalog and native learning

**Files:** create `src/Imlight.CoreLib/Game/Tower/TowerSpellProgression.cs` and `tower-spell-progression.json`; modify `TowerWizardDeckModel.cs`, catalog resource declarations, `TowerTomeComponent.cs` and existing Tower entry/award-delivery handlers. Tests: `TowerWizardDeckTests.cs`, new `TowerSpellProgressionTests.cs`, `TowerNativeWizardTests.cs`, harness `Program.cs`.

**Interfaces:** `TowerSpellProgression.IsEligible(uint templateId, string school, int level, long highestPermanentClear, bool primarySchool)` and a catalog lookup returning exact template/prerequisite/role metadata. Native learned spell persistence must use the same reconciled transaction/receipt path for automatic and manual grants.

- [ ] Add failing tests rejecting Mass Feint and unlisted variants regardless of candidate metadata; verify floor-3/5/8 rewards, enemy floor-4/6/9 eligibility and exact school exceptions from Task 1.
- [ ] Test primary quest spells without quest prerequisites, native-level alternative unlock, secondary prerequisite enforcement, repeated entry/relog idempotency and no changes to Training Points/wallet/equipped deck.
- [ ] Implement approved catalog and finite role-based deck composition using exact IDs, legal costs and supported effects. Retain low-level starter reliability and school identity.
- [ ] Implement automatic primary reconciliation on entry/level gains and free explicit secondary learning through Tome, with native spellbook/client synchronization and notifications.
- [ ] Run affected spell/native-asset/Tome/award/ordinary-training regressions; commit only after passing.

## Task 4: Compact permanent history and native wallet

**Files:** create `TowerClearHistory.cs` and `TowerArenaWallet.cs` under the Tower directory; modify `TowerAwardCollection.cs`, `TowerEconomyCollection.cs`, `TowerRunState.cs`, `CombatService.cs`, applicable native currency writers and reset/entry handlers. Tests: `TowerAwardTests.cs`, `TowerEconomyTests.cs`, `TowerPersistenceTests.cs`, new wallet/history tests and harness registration.

**Interfaces:** `TowerClearHistory.Contains(long floor)` / `Add(long floor)` use compact merged intervals plus durable receipts; wallet transaction helpers operate inside the existing Raven write session and expose committed native balance, not a parallel Tower balance.

- [ ] Test migration of legacy sparse clear arrays without inferring gaps; reset preserves history and rewards do not repeat. Verify interval merging and large floor values.
- [ ] Test rewards first=10B/repeat=3B at floors 1/10/11/cap/post-cap, including duplicate/reordered award delivery.
- [ ] Test one-time 1:1 custom balance migration added to existing native wallet, existing purchase receipts, simultaneous native spend/reward/migration, relog and capacity boundaries established in Task 1.
- [ ] Implement compact history migration, native wallet migration and atomic award/spend/receipt updates. Remove custom wallet mirroring from all active paths; reconcile live player stats after committed results without overwriting newer currency.
- [ ] Run Tower awards/economy/persistence/recovery plus affected ordinary currency regressions. Commit after passing; do not deploy a partially migrated purchase path.

## Task 5: Item-instance stat targets and functional Tome

**Files:** create `TowerItemUpgradeModel.cs` and `tower-item-upgrade-targets.json`; modify `TowerEconomyCollection.cs`, `TowerTomeComponent.cs`, item effect hydration and equipment stat-recalculation paths identified during Task 1. Tests: `TowerEconomyTests.cs`, new item upgrade tests, `TowerTomeServiceRoutingTests.cs`, native serialization tests.

**Interfaces:** upgrade preview contains item ID/template, fixed school, current/destination tier, exact delta, aggregate equipped before/after values, eligibility and native Ticket cost. Purchase validates the same item/equipment/tier state inside the wallet transaction.

- [ ] Test eight equipped slots, restricted/universal school binding, level+permanent-clear gates, ineffective-tier skips, cost=10B and no charge on stale preview/not-owned/unequipped/concurrent purchase.
- [ ] Test target gap fill preserving stronger stats, native special features/jewels, repeated recalculation, equip/unequip, relog, switching characters where native transfer is supported and native combat-stat use outside Tower.
- [ ] Test value-preserving legacy damage-upgrade conversion without repeat charges or stacked effects using Task 1's reviewed mapping.
- [ ] Implement approved stat targets and item-instance hydration; apply effective upgrade delta exactly once using native equipment effects. Reject unsupported upgrades before charging.
- [ ] Replace old Ticket spell purchases and damage-only menus with status/spell/milestone/equipment views, free secondary learning, native wallet display, exact previews and explicit confirmations. Preserve repeated Tome interaction and Reset behavior.
- [ ] Run item/economy/Tome/native serialization and affected ordinary equipment/training regressions. Commit the complete functional economy checkpoint after passing.

## Task 6: Full validation and isolated deployment

**Files:** maintain `docs/InfiniteGolemTower-Autonomous-Progress.md`; use existing development scripts unchanged unless an actual defect requires a separately tested fix.

- [ ] Build full `src/Imlight.sln -c Release --no-restore -m:1`; build console harness. Run all Tower modes and affected ordinary regressions, including installed native assets, gifts, embedded restart and loopback; inspect each exit code and log.
- [ ] Exercise representative low/middle/high/cap/post-cap profiles for stats, legal decks, rewards and upgrade budgets. Document initial balance limits; do not invent manual acceptance results.
- [ ] Review the complete tracked diff, verify unrelated work is preserved, commit coherent verified changes and push the authorized `tower` branch.
- [ ] Confirm development client closed, stop owned services, create verified saves backup and hash-verified rollback server snapshot. Publish Release, enable Tower only in private config, record hashes, start and observe readiness plus owned ports.
- [ ] Ask user to manually verify disabled cards/targeting with discard/Draw, labels, lower health, curated enemy spells, automatic grants/Tome secondary learning, native Blue Ticket increase, item preview/purchase/effect, equip switch/relog and reset/replay permanence.
- [ ] Record observed acceptance and fix reported failures. Mark complete only when the implemented paths and required manual acceptance are genuinely verified.
