# Infinite Golem Tower Progress

Last updated: 2026-10-12
Branch: `feature/infinite-golem-tower`
Worktree: `source/Imlight/.scratch/feature-infinite-golem-tower`
Latest tested commit: `c35bbbfc` (`fix: serialize embedded Raven startup`)
Current focus: Milestone H integration and client acceptance

## Implemented

- Private Floor 0 sanctuary, Tome and staircase routing. Logical floors reuse physical rooms 2–5; the sanctuary is room 1.
- Per-wizard run state, exclusive ownership/fencing, recovery checkpoints and unbounded logical floor navigation.
- Catalog-driven, native boss encounters with one boss-rank enemy per floor, Normal and Scaled Challenge modes, and level scaling.
- Tower-only player-first turn policy, defeat recovery and interruption handling.
- Atomic first-clear progression and reward receipts: one native level per unique floor clear below the cap, plus persistent repeat-clear Tickets.
- Award retries reconcile the prior receipt and inventory without repeating the client reward notice.
- Ticket ledger, school spell learning and bounded per-item equipment upgrades with effect recalculation and client synchronization.
- Tome floor, spell and equipment menus. Options now use native client locale keys and template display names. Purchases ask for a second selection and show Ticket cost or an upgrade preview before spending.

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
- Pointer hover visibly highlights the Spells row. The automated desktop click used for row-selection checks still produced service index 0 and toggled Challenge; this does not verify that a normal user click is misrouted, so non-first-row selection remains unverified.
- Floor 4's client reward notice appeared three times while the server logged one committed receipt. The handler also emitted notices for `AlreadyAwarded` results; it now sends the notice only for a newly committed receipt.
- After the startup fix, a fresh isolated Release publish from `4dbd1349` started RavenDB once and the stock client entered the world successfully. Admin resumed saved logical Floor 6 in recycled physical room 3 with one Lord Nightshade rank-3 boss.
- The stock client completed the Floor 6 duel (fight `4819513738710670409`). The server recorded a player Loremaster cast against slot 0, the enemy response, and a win. Exactly one durable Floor 6 receipt was committed for attempt 14: 1 Ticket, balance 3, highest floor 6. The run advanced to floor 7. The post-fight client capture was taken after the scene transition, so it does not establish how many reward toasts were visibly rendered; the receipt count and the notice guard regression are verified.
- Startup logs showed one embedded RavenDB initialization on the clean deployment, and there was no null-store Tower ownership failure.
- After the win, the server initialized a new session and reopened the saved run at floor 7. The stock client displayed `Tower floors: floor 7, highest cleared 6, Normal, 3 tickets`, confirming the saved floor and Ticket balance in the client after the transition.
- A stopped-server backup preceded each isolated development republish. Tower is enabled only in generated development configuration; source configuration remains disabled.

## Remaining acceptance work

- Client verification of non-first-row selection and the latest purchase-confirmation flow, including readable cost and upgrade previews without overlapping messages.
- Capture the client reward presentation during a clear, including any visible toast count.
- Play through defeat and extraction in the stock client and verify the return to Floor 0, retained progression, and room reuse.
- Verify first clear versus replay rewards, native level persistence after relog, Ticket balance persistence, spell purchase and actual spell use, equipment upgrade persistence and effect in combat.
- Exercise representative low-, mid-, high- and post-cap floors through the stock client. Automated floor-generation and economy tests pass, but this is not a substitute for those client runs.
- Recheck UI status/selection feedback and progression edge cases before claiming the gameplay contract complete.

## Next steps

1. Verify relogged floor and Ticket state after the Floor 6 clear.
2. Capture a reward notice during a client clear; verify Tome non-first-row selection and purchase previews, then test spell learning and equipment upgrades in real combat.
3. Continue the client journey through defeat/extraction and representative later floors; fix any failures found.
4. Push the tested Tower worktree commits to the authorized `tower` remote, leaving the unrelated root worktree and the untracked implementation plan untouched.

This is an active implementation. The automated and hub-menu checks do not establish full project completion.
