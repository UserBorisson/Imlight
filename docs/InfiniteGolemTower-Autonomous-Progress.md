# Infinite Golem Tower Progress

Last updated: 2026-10-12
Branch: `feature/infinite-golem-tower`
Worktree: `source/Imlight/.scratch/feature-infinite-golem-tower`
Latest tested source commit: `7db25089` (`fix(tower): reuse arena and present victory state`), pushed to the authorized `tower` remote; client acceptance is pending deployment.
Current focus: verify, commit, and deploy the single-room arena checkpoint, then continue with the curated enemy-wizard redesign.

## Implemented

- Private Floor 0 sanctuary, Tome and staircase routing. In the current local redesign, Floor 0 is room 1 and every combat floor reuses physical room 2; the Tome is centered at the room's surveyed `(0, -45)` position.
- Per-wizard run state, exclusive ownership/fencing, recovery checkpoints and unbounded logical floor navigation.
- Catalog-driven, native boss encounters with one boss-rank enemy per floor, Normal and Scaled Challenge modes, and level scaling.
- Embedded native boss catalog sourced from the installed Root.wad revision `r806919.Wizard_1_610`: 839 validated bosses across all 17 worlds, selected deterministically from the run seed within the configured floor world; the endless world rotation starts at floor 175.
- Tower-only player-first turn policy, defeat recovery and interruption handling.
- Atomic first-clear progression and reward receipts: one native level per unique floor clear below the cap, plus persistent repeat-clear Tickets.
- Award retries reconcile the prior receipt and inventory without repeating the client reward notice.
- Ticket ledger, school spell learning and bounded per-item equipment upgrades with effect recalculation and client synchronization.
- Tome floor, spell and equipment menus. Options now use native client locale keys and template display names. Purchases ask for a second selection and show Ticket cost or an upgrade preview before spending.
- The current local redesign keeps victory in the arena, unlocks the upper staircase after the durable clear, and forces a fresh encounter when the same physical room is reused for the next logical floor. Returning to Floor 0 retires the cached combat room and its transient actors.
- The Tome's empty-option response now resets its interaction state so the player can retry without first leaving its range. The source policy regression covers this retry path.
- A durable encounter ID guards victory presentation from stale messages across room reuse and replay. The victory-only presentation spawns a visual-only dazed copy of the defeated native NPC plus roaming native health and mana wisps; these presentation actors are excluded from combat.

## Verification

- Post-fix Release solution build: 0 errors, 24 warnings.
- Post-fix Tower regressions passed: `--tower-run`, `--tower-zone-policy`, `--tower-persistence`, `--tower-actor`, `--tower-duel`, `--tower-awards`, and `--tower-economy`.
- Post-fix gift regressions passed with the root content catalog: `--gift-eligibility`, `--gift-claims`, and `--gift-codec`.
- Fresh Release solution build: `src/Imlight.sln`, 0 errors and 24 warnings (dependency and existing API warnings).
- Fresh Release test harness build: 0 errors and 27 warnings.
- Passed Tower modes: `--tower-run`, `--tower-zone-policy`, `--tower-persistence`, `--tower-actor`, `--tower-duel`, `--tower-awards`, and `--tower-economy`.
- Passed gift regression modes: `--gift-eligibility`, `--gift-claims`, and `--gift-codec`. These catalog-based modes were run from the root workspace because the isolated Tower worktree does not contain the existing root `content/` catalog.
- Fresh Release solution build after the database startup fix: 0 errors, 14 existing warnings.
- New `--embedded-database-startup` regression first reproduced the race with 16 callers across two database singletons, then passed after synchronizing embedded server startup and each singleton's document-store cache.
- All seven Tower regression modes and all three gift regression modes passed after the startup fix.
- Stock client reached the private Floor 0 hub and opened the Tome. The menu displayed localized labels (`Challenge`, `Next`, `Spells`, `Equipment`) and boss names rather than blank rows.
- Stock client completed real Normal-mode duels on Floors 4 and 5. Floor 4 mapped to room 5 and Floor 5 reused room 2; each showed one Lord Nightshade rank-3 boss. A player Loremaster action resolved before victory, then the wizard returned to Floor 0 with Floor 5 and Floor 6 selected respectively. The server recorded highest clears 4 and 5 and Ticket balances 1 and 2.
- Live stock-client Tome routing check at saved Floor 7: pointer hover visibly highlighted Spells, and clicking it emitted `TowerTomeService_Floors_OpenSpells` with client index 0. The client displayed `Tower spells: floor 7, highest cleared 6, Normal, 3 tickets. Select a spell by its name.` This verifies the non-first-row action routes to Spells. Captures are in the parent workspace's local `development/diagnostics/tower-routing-fix-spells-hover.png` and `tower-routing-fix-spells-result.png`; the server trace is in `development/logs/application20261012.log` at 09:13:57 +02:00. The follow-up spell list and purchase preview remain unverified.
- Root asset audit scanned 14,633 object templates in the 17 world folders and found 850 boss-rank duelists; 11 scripted level-999 templates were excluded, leaving 839 unique candidates at levels 1–24, with at least four in every world. The previous 17 selected boss IDs remain represented in their world pools.
- The seeded catalog regression verifies world-band boundaries through floor 335, valid native paths and schools, stable selections for a run seed, and boss variation across run seeds. The Release solution build passed with 0 errors and 14 existing warnings; all Tower modes passed, including Tome routing and embedded database startup. Gift modes passed from the parent workspace where the content catalog is available.
- Floor 4's client reward notice appeared three times while the server logged one committed receipt. The handler also emitted notices for `AlreadyAwarded` results; it now sends the notice only for a newly committed receipt.
- After the startup fix, a fresh isolated Release publish from `4dbd1349` started RavenDB once and the stock client entered the world successfully. Admin resumed saved logical Floor 6 in recycled physical room 3 with one Lord Nightshade rank-3 boss.
- The stock client completed the Floor 6 duel (fight `4819513738710670409`). The server recorded a player Loremaster cast against slot 0, the enemy response, and a win. Exactly one durable Floor 6 receipt was committed for attempt 14: 1 Ticket, balance 3, highest floor 6. The run advanced to floor 7. The post-fight client capture was taken after the scene transition, so it does not establish how many reward toasts were visibly rendered; the receipt count and the notice guard regression are verified.
- Startup logs showed one embedded RavenDB initialization on the clean deployment, and there was no null-store Tower ownership failure.
- After the win, the server initialized a new session and reopened the saved run at floor 7. The stock client displayed `Tower floors: floor 7, highest cleared 6, Normal, 3 tickets`, confirming the saved floor and Ticket balance in the client after the transition.
- A stopped-server backup preceded each isolated development republish. Tower is enabled only in generated development configuration; source configuration remains disabled.
- Fixed enemy support targeting to use each friendly subcircle's raw zero-based sigil slot. The new `--tower-duel` regression failed before the fix and passed afterward; the full Release solution build passed with 0 errors and 24 warnings.
- The user manually advanced the stock client through Floor 16. Server logs record the Floor 16 victory, a 19-Ticket balance, and creation of logical Floor 17 in a recycled physical room. No development deployment or client/server restart was performed for the AI fix.
- Current single-room source verification: Release build of `src/Imlight.sln` succeeded with 0 errors and 14 existing warnings; the Release customization test harness built with 0 errors and 17 existing warnings.
- Current single-room Release regressions passed: `--tower-run`, `--tower-zone-policy`, `--tower-persistence`, `--tower-actor`, `--tower-duel`, `--tower-awards`, `--tower-economy`, `--tower-tome-routing`, `--embedded-database-startup`, default loopback checks, and `--gift-eligibility`, `--gift-claims`, `--gift-codec`.
- The new room-eviction actor integration test passed and observed Floor 0 routing evict the cached room 2 actor. The deployment currently running in the isolated environment predates these source changes; client behavior for the new single-room flow, dazed display, and wisp collection remains unverified.
- Commit `7db25089` contains the verified single-room source milestone and is pushed to `tower/feature/infinite-golem-tower`. The preexisting untracked plan file remains outside the commit.

## Remaining acceptance work

- Deploy the verified single-room build from the feature worktree to the isolated environment after a fresh stopped-server save backup and a rollback copy of the current server binaries.
- Client acceptance for enemy-free Floor 0, centered/reopenable Tome, one physical combat room, natural post-victory movement, dazed display, collectible roaming wisps, both staircases, logical-floor room reuse, defeat return, and replay without duplicate first-clear leveling.
- Client verification that the spell submenu can be reopened after its summary response, plus the latest purchase-confirmation flow with readable cost and upgrade previews.
- Capture the client reward presentation during a clear, including any visible toast count.
- Play through defeat and extraction in the stock client and verify the return to Floor 0, retained progression, and room reuse.
- Verify first clear versus replay rewards, native level persistence after relog, Ticket balance persistence, spell purchase and actual spell use, equipment upgrade persistence and effect in combat.
- Exercise representative low-, mid-, high- and post-cap floors through the stock client. Automated floor-generation and economy tests pass, but this is not a substitute for those client runs.
- Recheck UI status/selection feedback and progression edge cases before claiming the gameplay contract complete.

## Source audit findings

- Defensive AI can leave its default hand selection when a buff is available, a coin flip selects the debuff branch, and no debuff is available. Its healing path also overwrites the smart highest-pip choice with a later random choice.
- Boss manifests still provide no spell profile (`SpellNames` is empty), so scaled boss stats do not select a level-appropriate native deck.
- Equipment upgrades have persistence coverage, but no end-to-end regression yet proves effect hydration, equip switching, and changed combat damage.

## Next steps

1. Create a fresh isolated-development backup while stopped, retain a copy of the current server binaries for rollback, build from this worktree, and verify the generated development-only Tower setting.
2. Complete actual-client acceptance of the single-room arena, Tome, stairs, victory, wisps, room reuse, and replay; fix anything observed before calling it playable.
3. Once that checkpoint is genuinely playable, replace generic creature selection with curated native wizard-like opponents and floor-tier-based player-like statistics plus simulated invisible gear.
4. Fix camera/animation transitions, implement the allowed enemy-planning hand operations, finite level-appropriate decks and tactical AI, then correct the identified defense/healing choices.
5. Finish native shared Arena Ticket wallet, training-cost, owned-item upgrade hydration/equip-switch, and scalable durable clear progression, with regression coverage.
6. Continue client acceptance for Tome purchases, first-clear/replay rewards, defeat/extraction, relogging, and representative low-, mid-, high-, and post-cap floors; then push verified commits to the authorized `tower` remote while leaving unrelated worktrees and the untracked implementation plan untouched.

This is an active implementation. The automated and hub-menu checks do not establish full project completion.
