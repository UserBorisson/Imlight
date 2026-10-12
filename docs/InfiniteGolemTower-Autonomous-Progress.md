# Infinite Golem Tower Progress

Last updated: 2026-10-12
Branch: `feature/infinite-golem-tower`
Worktree: `source/Imlight/.scratch/feature-infinite-golem-tower`
Latest tested commit: `6a2ba20317583dafb2f825223e91f8975cd4ccf8`
Current focus: Milestone H integration and client acceptance

## Implemented

- Private Floor 0 sanctuary, Tome and staircase routing. Logical floors reuse physical rooms 2–5; the sanctuary is room 1.
- Per-wizard run state, exclusive ownership/fencing, recovery checkpoints and unbounded logical floor navigation.
- Catalog-driven, native boss encounters with one boss-rank enemy per floor, Normal and Scaled Challenge modes, and level scaling.
- Tower-only player-first turn policy, defeat recovery and interruption handling.
- Atomic first-clear progression and reward receipts: one native level per unique floor clear below the cap, plus persistent repeat-clear Tickets.
- Ticket ledger, school spell learning and bounded per-item equipment upgrades with effect recalculation and client synchronization.
- Tome floor, spell and equipment menus. Options now use native client locale keys and template display names. Purchases ask for a second selection and show Ticket cost or an upgrade preview before spending.

## Verification

- Fresh Release solution build: `src/Imlight.sln`, 0 errors and 24 warnings (dependency and existing API warnings).
- Fresh Release test harness build: 0 errors and 27 warnings.
- Passed Tower modes: `--tower-run`, `--tower-zone-policy`, `--tower-persistence`, `--tower-actor`, `--tower-duel`, `--tower-awards`, and `--tower-economy`.
- Passed gift regression modes: `--gift-eligibility`, `--gift-claims`, and `--gift-codec`. These catalog-based modes were run from the root workspace because the isolated Tower worktree does not contain the existing root `content/` catalog.
- Stock client reached the private Floor 0 hub and opened the Tome. The menu displayed localized labels (`Challenge`, `Next`, `Spells`, `Equipment`) and boss names rather than blank rows.
- Pointer hover visibly highlights the Spells row. The automated desktop click used for row-selection checks still produced service index 0 and toggled Challenge; this does not verify that a normal user click is misrouted, so non-first-row selection remains unverified.
- A stopped-server backup preceded each isolated development republish. Tower is enabled only in generated development configuration; source configuration remains disabled.

## Remaining acceptance work

- Client verification of non-first-row selection and the latest purchase-confirmation flow, including readable cost and upgrade previews without overlapping messages.
- Play through a real boss encounter and verify player action, enemy response, victory, defeat, extraction, and room-reuse behavior in the stock client.
- Verify first clear versus replay rewards, native level persistence after relog, Ticket balance persistence, spell purchase and actual spell use, equipment upgrade persistence and effect in combat.
- Exercise representative low-, mid-, high- and post-cap floors through the stock client. Automated floor-generation and economy tests pass, but this is not a substitute for those client runs.
- Recheck UI status/selection feedback and progression edge cases before claiming the gameplay contract complete.

## Next steps

1. Verify Tome row selection with reliable stock-client input, then confirm purchase previews and enter a combat floor.
2. Follow the run through player action, enemy response, clear or defeat, extraction, and room reuse.
3. Continue the client journey through Tickets, spell learning, equipment upgrades, relog, and representative later floors; fix any failures found.
4. Commit and push the tested Tower worktree changes to the authorized `tower` remote, leaving the unrelated root worktree and the untracked implementation plan untouched.

This is an active implementation. The automated and hub-menu checks do not establish full project completion.
