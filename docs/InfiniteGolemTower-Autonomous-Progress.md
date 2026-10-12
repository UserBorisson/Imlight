# Infinite Golem Tower Progress

Last updated: 2026-10-13
Branch: `feature/infinite-golem-tower`
Worktree: `source/Imlight/.scratch/feature-infinite-golem-tower`
Last deployed source commit: `d1d0b1c6`, pushed to `tower/feature-infinite-golem-tower`.
Current focus: user confirmed the progression requirements and initial health, utility-milestone and Ticket/upgrade rules. Written implementation plan is ready for review before production changes. Native-content audit will resolve exact client controls, nameplate schema, wallet identity, approved spell IDs and equipment targets; existing deployment remains unchanged.

## 2026-10-13 — confirmed progression design

- Execution approved after written plan review. First source checkpoint now sends native `MSG_SHOWCOMBATUI.AltTurn/AltTurnTeam`, advances participant turn flags, enables native rank presentation with public logical-floor level, applies the approved lower HP curve and removes Normal post-cap HP growth. The harness build and all 14 existing regression modes passed after failures reproduced the missing UI payload, old health and hidden-rank behavior. This checkpoint is not deployed and client acceptance is pending. Native wallet/content/item work continues.

- Confirmed automatic primary-school learning, free selected secondary-school learning, no Training Point costs, school quest spells at approved native levels without quest chains, permanent learned spells and no automatic deck edits.
- Confirmed floor-3 blade, floor-5 trap and floor-8 support milestones; enemies mirror previously earned milestones on floors 4/6/9. Exact approved variants require the installed-content audit; metadata alone cannot authorize a card.
- Confirmed Normal HP factors .85/.90/1.00/1.05/1.10/1.15 at levels 1/10/30/50/100/native cap, interpolated and capped. Post-cap Normal stats remain capped.
- Confirmed native Blue Arena Ticket migration of remaining custom balance at 1:1, rewards of 10B first-clear total and 3B replay, per-item target tier cost 10B, and permanent first-clear history through resets. B is the capped ten-floor band.
- Confirmed equipped eight-slot upgrades that fill toward school/slot targets, preserve stronger native stats and special properties, retain item identity and fixed upgrade school, and work outside Tower. Target levels 1/10/20/.../cap require character level L and permanent clear L−1. Starter gear should remain viable to cap; no refunds/transfers initially.
- Design: `docs/superpowers/specs/2026-10-13-tower-progression-and-economy-design.md`; implementation plan: `docs/superpowers/plans/2026-10-13-tower-progression-and-economy.md`. These documents do not establish implemented behavior or client acceptance.

## 2026-10-13 — user acceptance and requested design clarification

- User confirmed player-wizard appearance/player-first flow, enemy response and choreography, and same-body daze/post-victory behavior on the deployed checkpoint.
- Enemy-planning hand remains visible but permits clicking spells and targeting the enemy. Server-side cast rejection does not satisfy the intended client behavior: cards must appear disabled for casting and enemy targeting must be disabled while discard/Treasure Card interaction remains permitted.
- Enemy nameplates must communicate wizard level and school. User reports excessive enemy health and requests substantial reduction.
- User reports Blue Arena Tickets are not awarded and Tome spell teaching/equipment upgrading are not available as required. Prior custom economy regression coverage does not establish native wallet or usable client Tome integration.
- An enemy used Mass Feint; internal card-level filtering is insufficient. Enemy decks need explicit, reasonable gradual power progression.
- User explicitly requests extensive design clarification before further autonomous implementation. Native wallet, training, gear progression and curated enemy progression are pending design decisions; no new client acceptance claims extend beyond the confirmations above.

## 2026-10-13 — player-avatar deployment evidence

- Published Release source `d1d0b1c6b5a7ea0c9a18cfdc101c13a0a87075b8` after the full solution/harness builds and 14 regression modes recorded below. Tracked source was clean; the preexisting untracked implementation plan remains preserved.
- Stopped development services and verified fresh saves backup `development/backups/20261012T221757205Z`. The same backup contains a hash-verified 458-file `rollback-server` copy and `rollback-server.json` with the previous build manifest. No progression or Ticket reset was performed during deployment.
- Published Director SHA256: `8AD036BD361BAB3DDF6B731A7E552429D97ACD4B75C4093F8478E36ED06DF695`; Core SHA256: `60F77F73756CC3FAC6D1318EA96237B0958B01CFDF34B51FDA587308542E693F`.
- Tower is enabled only in the generated private development config, SHA256 `475A14075DDC026856F87A7F7A756B3DD879526569D2829B84C0ACF1248A8C74`; source configuration remains disabled. Fresh post-start hashes matched the build manifest.
- Observed readiness at local time `2026-10-13 00:21:16`: director PID `31388`, expected executable, all three owned listeners on `127.0.0.2` ports `13000`, `13333`, `13500`. Development port ownership checks passed.
- Client acceptance remains pending. The user should check native wizard appearance, player-first flow, enemy response after 2–7 seconds, complete cast/camera sequencing, visible hand with persistent discard/Treasure Card edits during enemy planning and casting blocked, then same-body daze, movement, wisps and stairs after victory. Startup and automated checks do not establish these visual/input results.

## 2026-10-13 — native player-avatar combat checkpoint

- Clarified contract: actual customizable player wizard bodies controlled by AI, stable per-floor identity, player-first alternating planning/action phases, enemy planning randomly 2–7 seconds, persistent player discard/Treasure Card hand edits during enemy planning, no player casting in that phase, complete native player cast choreography, and the same defeated body remaining dazed until room retirement. The later instruction to retain native pip/ongoing-effect ordering supersedes the earlier proposed custom timing.
- Native player schema replaces world-NPC rendering. Server proximity/deck/AI/statistics adapters remain ephemeral and do not create persistent wizard, account, inventory or player-session records. Fenced enemy admission and authenticated human ownership remain distinct despite both bodies using client template 1. Names use existing native localization so the opponent and Tome labels agree.
- Enemy AI is prompted after visible planning begins. It cannot replace the 2–7 second planning timer with the former one-second shortcut. The player receives the current hand during enemy planning without a deck refill; discards and Treasure Card draws persist, and player spell requests are rejected. Existing native cycle pip gains and per-caster ongoing-effect resolution are retained.
- A sparse school-stat lookup regression reproduced an exception before spell resolution; missing native school entries now contribute zero while present entries remain usable. An insufficient-pip attack now returns after queuing Pass. Healing uses fractional health, smart healing choices are retained, and buff-only defensive hands select an available buff.
- Defeat preserves the authoritative player-avatar object, disables its proximity admission/AI, and retains/replays the native looping Dazed state. A regression failed before retained-state support and passes after the fix. Roaming health/mana wisps remain separate transient room actors; the former duplicate defeated NPC is skipped for player-avatar opponents.
- Installed-asset coverage reads development Root.wad without modifying caches or saves. It checks native template-1 behavior schema, names matching Tome labels, production render serialization, native base/simulated equipment statistics and enemy accuracy resolution on floors 1, 14, 65, 100, 170, 200 and 10000. A name/label mismatch was reproduced and corrected before deployment.
- Independent code review inspected native schema/admission separation and identified the retained-daze gap, now fixed. A possible future Tower minion planning issue does not affect supported Tower behavior: Tower minion summons are explicitly disabled. The review agent then hit its usage limit; no complete final review verdict was obtained.
- Verification: full Release solution build passed (0 errors, 14 existing warnings), harness build passed, and all 14 regression modes passed: native wizard assets, Tower run, zone policy, persistence, actor routing, duel lifecycle, awards, economy, Tome routing, embedded startup, ordinary gift eligibility/claims/codec and loopback. Logs are under `development/logs/tower-wizard-final-*`. Client acceptance is pending: source/asset checks do not establish camera, appearance, hand-control or defeat-animation acceptance. Deployment evidence follows when observed. The older implementation history below describes earlier checkpoints where applicable.

## Implemented

- Private Floor 0 sanctuary, Tome and staircase routing. In the current local redesign, Floor 0 is room 1 and every combat floor reuses physical room 2; the Tome is centered at the room's surveyed `(0, -45)` position.
- Per-wizard run state, exclusive ownership/fencing, recovery checkpoints and unbounded logical floor navigation.
- Catalog-driven, native boss encounters with one boss-rank enemy per floor, Normal and Scaled Challenge modes, and level scaling.
- Curated wizard identities sourced from installed Root.wad revision `r806919.Wizard_1_610`: 13 names and seven schools selected deterministically by floor, independently of run seed. Their world-NPC templates are server combat adapters only; the client receives native player template 1 and player customization/name/school behaviors. The 17 world bands describe progression and rotate from floor 175.
- Normal-mode opponents use floor-tier native player base statistics with invisible simulated equipment, independent of the wizard's level/upgraded gear. Scaled Challenge explicitly adapts to wizard level. Finite native school decks follow trainer levels and guarantee native starter attacks; original high-rank NPC/equipment decks are excluded.
- Confirmed Tome Reset restarts floor access at Floor 1, preserves Tickets and character progression, and retains historical first-clear eligibility to prevent duplicate levels. Tower-enabled Commons/Court entrances remove their story-quest gate without completing quests.
- Tower-only player-first turn policy, defeat recovery and interruption handling.
- Atomic first-clear progression and reward receipts: one native level per unique floor clear below the cap, plus persistent repeat-clear Tickets.
- Award retries reconcile the prior receipt and inventory without repeating the client reward notice.
- Ticket ledger, school spell learning and bounded per-item equipment upgrades with effect recalculation and client synchronization.
- Tome floor, spell and equipment menus. Options now use native client locale keys and template display names. Purchases ask for a second selection and show Ticket cost or an upgrade preview before spending.
- The current local redesign keeps victory in the arena, unlocks the upper staircase after the durable clear, and forces a fresh encounter when the same physical room is reused for the next logical floor. Returning to Floor 0 retires the cached combat room and its transient actors.
- The Tome's empty-option response now resets its interaction state so the player can retry without first leaving its range. The source policy regression covers this retry path.
- The Tome resets cached submenu, page, option and confirmation state when its player leaves interaction range or disconnects; the persistent sanctuary actor no longer reopens in a stale Equipment or Spells submenu after relog.
- Durable run saves now retain the post-victory stair-unlock flag and cleared encounter identity, which are needed for the upper-stair gate and the dazed presentation after reload.
- The first ascent from Floor 0 applies a one-time roster migration in a fenced cluster-wide transaction: it starts a new Floor 1 run, resets saved Tower clears and Tickets, and preserves native level, experience, spells, owned items, item upgrades and durable award receipts. Historical unreceipted awards remain recorded but their old roster version cannot be delivered or committed after the reset. Later runs inherit the reset version and do not wipe new progress.
- A durable encounter ID guards victory presentation from stale messages across room reuse and replay. The victory-only presentation creates a display-only copy and roaming native health and mana wisps. The native client template schema is preserved while server components are restricted to rendering and animation. The user confirmed the dazed copy, collectible wisps and post-victory control on `76b7ab73`; immediate HP-zero native wizard dazing remains follow-up work.
- Tome floor and challenge changes can now discard a materialized encounter only while it remains unstarted. This handles returning from an entered floor without having fought; an admitted duel's manifest cannot be discarded. The regression covers both paths.

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
- Latest working-tree verification (2026-10-12): Release solution build and Release customization harness build succeeded with 0 errors; the solution reported 14 existing warnings.
- The final Tower regression pass succeeded for `--tower-run`, `--tower-zone-policy`, `--tower-persistence`, `--tower-actor`, `--tower-duel`, `--tower-awards`, `--tower-economy`, `--tower-tome-routing`, and `--embedded-database-startup`. The embedded database regression used the repo's local .NET SDK on `PATH`.
- Ordinary regressions passed for `--gift-eligibility`, `--gift-claims`, `--gift-codec`, and the default loopback listener. The content-catalog gift checks ran from the parent workspace because this worktree does not include `content/`.
- Latest verification after the Tome and presentation fixes: full Release solution build passed (0 errors, 14 warnings); Release customization harness build passed (0 errors, 17 warnings). All Tower modes passed: `--tower-run`, `--tower-zone-policy`, `--tower-persistence`, `--tower-actor`, `--tower-duel`, `--tower-awards`, `--tower-economy`, `--tower-tome-routing`, and `--embedded-database-startup`. Ordinary `--gift-eligibility`, `--gift-claims`, `--gift-codec`, and the default loopback listener passed. The embedded database mode requires the repository-local .NET SDK directory on `PATH`.
- The presentation regression failed before the fix because the dynamic dazed copy did not retain the native client behavior schema; it now passes with the server-side component allowlist. The Tome run-state regression failed before the fix because a generated but unstarted Floor 3 manifest prevented mode and floor changes after returning to Floor 0; both Tome reconfiguration and preservation of admitted encounters now pass.
- The persistence regression first reproduced that a victorious run lost `PendingFloorAdvance` after reload. It now passes for the stair flag and cleared encounter identity, and exercises the one-time reset under the RavenDB writer lease, including old-award suppression, reset idempotency, progress preservation, and reset-version inheritance.
- The Tome routing regression failed before the transient menu state existed and passes after the player-leave cleanup was wired through the interaction memento.
- The new room-eviction actor integration test passed and observed Floor 0 routing evict the cached room 2 actor.
- Commits `840a92f8` (Tome session cleanup) and `8cc6907c` (victory persistence and one-time progression reset) are pushed to `tower/feature-infinite-golem-tower`.
- Deployed source commit `8cc6907c` to the isolated development server. The Release publish completed, and the Tower setting is enabled only in the generated development config; the feature source config remains disabled. The build manifest records generated config SHA256 `09A94AFD71F32914E2D1E8CB5642F687D762236AFC6B2D3754D8961E29F2B`.
- Before deployment, the stopped-server backup procedure verified save backup `development/backups/20261012T185525368Z` and a hash-checked rollback copy of the prior server (458 files, 644,176,387 bytes).
- Stock client entered Floor 0 after deployment. The sanctuary was visibly enemy-free, the Tome was centered and opened to its menu. The last observed Tome status showed the wizard's legacy Floor 24, 23 clears, and 36 Tickets; the first-ascent reset has not yet been observed. Full manual client acceptance remains pending.
- Commit `7db25089` contains the verified single-room source milestone and is pushed to `tower/feature/infinite-golem-tower`. The preexisting untracked plan file remains outside the commit.

## Earlier client acceptance findings (before the wizard checkpoint)

- The user's manual run after the one-time reset reached Floor 1 Fire Crow rank 3 Fire and Floor 2 Great Troll rank 3 Myth. The lower stairs returned to Floor 0, the Tome showed current progress, selecting Fire Crow selected Floor 1, the upper stairs advanced to Rattlebones rank 14 Death, and replay was possible. The wizard remained controllable after victories and received floor/reward messages; the user did not visually confirm reward totals.
- The Tome rejected a challenge-mode change after entering Floor 3 and returning without combat. The run retained the unstarted manifest, now discarded by eligible Tome floor/mode changes.
- After victory the boss disappeared and the user saw one health and one mana wisp; the dazed copy was not visible. Client logs recorded `MSG_NewObject` deserialization failures at victory, matching the shortened template schema fixed in commit `76b7ab73` and included in the current deployment. The fix has automated coverage but still needs client verification.
- The user also reported a stale server message on first returning to the Tome; entering and exiting a floor refreshed it and showed the Tower reset message. The deployed Tome fix still needs confirmation that the current menu and server feedback update correctly after the unstarted-floor return path.
- The roster is still generic creatures: the client logged `Heroic_WC_Rattlebones_100`. Floor 3's deck also warned that the native creature had no usable spells and fell back to a default spellbook. One-shot fights did not verify an enemy action phase, so the combat-flow concern remains open.

## Previous deployment verification

- Commit `76b7ab7333718269d7b7a3cb6918d87f4b641df8` was built and published from the Tower worktree as a Release build. The only dirty worktree item at publish time was the intentionally preserved untracked implementation plan.
- A stopped-server backup was created at `development/backups/20261012T193821230Z`; its rollback-server copy has a hash manifest and contains 458 files totaling 644,280,074 bytes.
- The deployed manifest reports Tower enabled and source commit `76b7ab7333718269d7b7a3cb6918d87f4b641df8`. Fresh SHA256 checks matched both published binaries and the generated development config. Tower remains disabled in source config.
- The authorized development-port ownership check passed after startup. The director process is the expected `development/server/Imlight.Director.exe`; no client acceptance was performed by the agent.
- Release solution and customization-harness builds and the listed Tower/gift/loopback regressions passed before this deployment. The deployment itself did not change source after those checks.

## Remaining acceptance work

## Wizard checkpoint deployment — 2026-10-12

- Deployed Release source `479531a14c3116d909a31089ab7d9b285f45d09c`. The preserved untracked implementation plan was the only dirty worktree item at publication. Four coherent commits cover floor reset (`0831c0cb`), level-one entrance access (`27aec2dd`), finite-card consumption (`d41f5e55`) and wizard roster/stats/decks (`479531a1`); all are pushed to the authorized Tower remote.
- Verified stopped-server save backup: `development/backups/20261012T204414275Z`. The same directory contains a hash-checked 458-file `rollback-server` copy and manifest of the prior deployed build.
- Fresh published hashes match the build manifest: director `D5C0A937D0894DAD03C83D98D56180DA1611BF578F6E2CD634AE58E98DDCD952`; core `C6CDD7F74DB3966B00BC8866F7BA2893333430BFBF3BB3CB2615FDB623DD7E1C`; generated config `475A14075DDC026856F87A7F7A756B3DD879526569D2829B84C0ACF1248A8C74`. Tower is enabled only in generated development configuration; source remains disabled.
- Development startup reached “Imlight may now be connected to”; login/game/patch listeners and authorized port ownership passed. No client inputs or new client acceptance were performed by the agent.

### 2026-10-12 user acceptance and current implementation update

- The user's latest manual run confirms intended floor navigation, the visible dazed copy, collectible health/mana wisps, and controllable post-victory movement. Reward text appears correct; XP/first-clear levels remain untested in the client. Blue Arena Tickets do not increase: native shared-wallet integration remains outstanding.
- Enemy planning/action phases are functional in the client, but enemy casting choreography is visibly desynchronized: the camera misses sigil drawing and spell playback starts/ends before the sigil completes. This remains an open defect. Player choreography is reported correct.
- User confirmed the Tome Reset policy: reset cleared/unlocked floors only; preserve shared Arena Tickets, native level, spells, equipment, and upgrades. Preserve historical first-clear reward eligibility so resetting cannot grant duplicate levels.
- Added a fenced floor-reset transaction and a native Tome Reset row (`GUI_00010632`, audited as “Reset” in Root's English GUI table). Selection requires confirmation within 30 seconds against the same run epoch/revision. Leaving interaction clears confirmation. Active combat, stale snapshots, and unsettled award outboxes reject reset. The transaction changes floor access and fences previous room snapshots while retaining Tickets and first-clear history. The older one-time, explicitly authorized migration is a separate operation.
- Added Tower-enabled entrance policy for Commons-to-Court and Court-to-sanctuary. The native Commons `TeleportToGolemCourt` quest requirements were the server-side entry blocker. Only single-teleport entrance triggers have their requirements cleared after database destination replacement; disabled Tower and unrelated/combat routes retain native requirements. No story quests are completed by this change.
- Verification observed: the access regression failed before implementation and passed afterward; the floor-reset regression failed before the transaction existed and passed afterward. The final reset rerun passed, including pending-award rejection and persisted revision advancement. Release solution build passed (0 errors, 14 existing warnings); Release harness build passed (0 errors, 17 existing warnings). All nine Tower/startup modes passed: run, zone-policy, persistence, actor, duel, awards, economy, Tome routing, embedded database startup. Ordinary gift eligibility, claims, codec and default loopback checks passed. These checks include the current roster/stat worktree changes; the roster is still awaiting its deck implementation before deployment.
- Source wizard checkpoint: curated 13 native human wizard NPCs covering all seven schools, retaining native Dragonspyre models and school identity; world bands now describe progression tiers rather than model provenance. Normal floor level follows floor tier; Scaled Challenge alone uses wizard level. Native player base stats plus configurable simulated-equipment budgets replace monster HP/effects for Tower opponents. These initial equipment budgets still need gameplay calibration.
- Tower decks now use finite native school cards eligible by trainer level, with four audited native starter attacks guaranteed for every school. Low floors cannot inherit the original rank-eight NPC/equipment deck. Decks currently cap at 58 cards and exclude Treasure, X, shadow and school-pip spells until corresponding enemy planning/resource support is completed. Tactical deck composition, reshuffle decisions and advanced-cost spell support remain follow-up combat work.
- A new finite-deck regression reproduced a shared ordinary/Tower bug: drawing a card removed it from the remaining pile, then discarding it removed another copy. The regression failed (remaining copies expected 2, actual 1), and passes after removing the second decrement. Four copies now produce exactly four draws. The final Release solution build passed (0 errors, 14 warnings), harness build passed, all nine Tower/startup modes passed, and ordinary gift eligibility/claims/codec plus loopback passed after these changes.
- Current deployed source is `479531a1`; none of the wizard/reset/level-one access changes has client acceptance yet. Level-one Court/Tower entry on both existing and newly created characters is pending manual acceptance.

- Verify that the first upward entry resets this wizard to Floor 1 with zero Tower clears and Tickets, preserves native level, spells and equipment upgrades, and does not reset again on later visits.
- Manually verify the deployed Tome return-trip and victory-presentation fixes, including current Tome feedback after a no-combat return, a visible dazed copy, separate roaming wisps and pickup, upper-stair progression, and replay without duplicate first-clear leveling.
- Continue client acceptance for enemy-free Floor 0, centered/reopenable Tome, one physical combat room, natural post-victory movement, both staircases, logical-floor room reuse, and defeat return.
- Client verification that the spell submenu can be reopened after its summary response, plus the latest purchase-confirmation flow with readable cost and upgrade previews.
- Capture the client reward presentation during a clear, including any visible toast count.
- Play through defeat and extraction in the stock client and verify the return to Floor 0, retained progression, and room reuse.
- Verify first clear versus replay rewards, native level persistence after relog, Ticket balance persistence, spell purchase and actual spell use, equipment upgrade persistence and effect in combat.
- Exercise representative low-, mid-, high- and post-cap floors through the stock client. Automated floor-generation and economy tests pass, but this is not a substitute for those client runs.
- Recheck UI status/selection feedback and progression edge cases before claiming the gameplay contract complete.

## Source audit findings

- Resolved in the native player-avatar checkpoint: buff-only defensive hands, smart healing selection and fractional-health evaluation. Advanced tactical choices and balance remain pending.
- Finite native trainer-level school decks are implemented through the server deck adapter; `SpellNames` remains empty by design. Tactical composition, reshuffle decisions and advanced pip-cost support remain pending.
- Equipment upgrades have persistence coverage, but no end-to-end regression yet proves effect hydration, equip switching, and changed combat damage.

## Next steps

1. Complete actual-client acceptance of the deployed Tome return-trip fix and victory presentation alongside the already observed stairs, floor reuse, and replay; fix anything observed before calling the checkpoint playable.
2. Accept actual native player-avatar opponents and floor-tier statistics/simulated gear, then refine tactical identities and deck composition using observed combat.
3. Fix camera/animation transitions, implement the allowed enemy-planning hand operations, finite level-appropriate decks and tactical AI, then correct the identified defense/healing choices.
4. Finish native shared Arena Ticket wallet, training-cost, owned-item upgrade hydration/equip-switch, and scalable durable clear progression, with regression coverage.
5. Continue client acceptance for Tome purchases, first-clear/replay rewards, defeat/extraction, relogging, and representative low-, mid-, high-, and post-cap floors; then push verified commits to the authorized `tower` remote while leaving unrelated worktrees and the untracked implementation plan untouched.

This is an active implementation. The automated and hub-menu checks do not establish full project completion.
