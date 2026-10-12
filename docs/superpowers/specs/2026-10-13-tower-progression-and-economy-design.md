# Tower combat controls, spell progression and native economy

Status: requirements and proposed health/milestone/economy rules confirmed by the user on 2026-10-13. Exact native-content tables remain subject to the audit and review gate below.

## Accepted requirements

- Enemy planning: gray cards, no left-click selection or enemy targeting; right-click discard and Treasure Card drawing remain available and persist. Server validation independently rejects casting.
- Enemy overhead: name above; school icon followed by `Level [logical floor]` below. Internal native level is capped independently of this display.
- Normal enemy level equals logical floor until the installed native cap. Stats are independent of player gear. Above the cap, stats remain capped; tactics and deck composition provide variation and difficulty within a finite ceiling.
- Enemy health should be slightly below a modestly equipped same-level player, with native school differences and no boss multiplier. No fixed fight-duration requirement.
- Spell unlocks use an explicit reviewed exact-template-ID catalog. Initially enemies use regular learned school spells; unapproved item/Treasure/mass/boss variants are excluded. Both sides use the same approved early school-utility unlock schedule.
- Primary-school spells are learned automatically, including native school quest spells at their approved normal levels without quests or prerequisite chains. Reconcile missing spells on Tower entry and level gain. Preserve learned spells and Training Points; charge neither Training Points nor Tickets.
- Secondary-school spells are selected freely in the Tome at approved native levels with spell prerequisites. Primary-school quest exclusives are not thereby granted to other schools. No automatic deck edits.
- Selected utility spells may unlock through permanent Tower milestones before native levels. Native levels remain an alternative unlock. Major damage/healing unlocks remain near native progression. Learned spells work everywhere and survive Reset Tower.
- Native Blue Arena Tickets are the only upgrade currency, shared with ordinary native wallet consumers. Convert remaining custom Tower balance once, 1:1, without replacing existing native balance or reviving spent Tickets.
- Every victory awards tier-based repeat Tickets; first-ever clears have an additional permanent bonus. No daily limits or overlevel penalties. Reset retains first-clear history, character progression, learned spells, item upgrades and wallet.
- Upgrade only equipped hat, robe, boots, wand, athame, amulet, ring and deck initially. Upgrades belong to the item instance and work everywhere. No refunds/transfers initially.
- Starter gear can remain competitive to the native cap. Fill gaps toward slot/school tier targets; preserve stronger native stats. Universal gear binds its upgrade school to the upgrading wizard; native school restrictions are respected. Store that school on the item.
- Upgrade ordinary numeric combat stats; do not change item cards, sockets, jewels, starting pips or power/shadow-pip mechanics in this milestone.

## Confirmed initial numerical rules

### Enemy health

Normal maximum HP = round(native same-school base HP at effective level × health factor).

| Native level | Factor |
| --- | --- |
| 1 | 0.85 |
| 10 | 0.90 |
| 30 | 1.00 |
| 50 | 1.05 |
| 100 | 1.10 |
| Native cap | 1.15 |

Interpolate between anchors, never apply equipment health twice, and cap at the native-cap anchor. These are proposed initial balance values, not verified player-equipment measurements. Keep school base-health differences. If the installed cap differs from 170, use the installed cap and maintain increasing anchor levels. At level 170 this is approximately 38% below the current 1.85× profile; at level 1 it is approximately 23% below the current 1.10× profile. Scaled Challenge retains an explicitly separate policy; it must not leak into Normal.

### Early utility milestones and enemy decks

- First clear of floor 3: regular primary-school blade, where an approved learned variant exists.
- First clear of floor 5: regular primary-school single-target trap, where one exists.
- First clear of floor 8: one approved school-appropriate defensive/support utility.
- Native-level eligibility grants these without needing the milestone. Do not invent missing school equivalents or substitute a stronger mass/item/Treasure variant.
- Enemy floor 4 can use floor-3 milestone utilities, floor 6 floor-5 utilities and floor 9 floor-8 utilities: mirror what a player has earned before entering that encounter, rather than granting an opponent the reward for defeating itself.
- Floor 1 starts with the audited school starter and legal basic actions. Introduce ordinary low-pip attacks and approved basic defenses gradually, then school-specific combinations as their explicit unlock levels are reached.
- Exact native spell IDs, normal unlock levels, effect-support checks and school exceptions must be recorded in a versioned catalog. Candidate metadata may locate spells but never authorize eligibility. Mass Feint and all other unapproved variants are denied by default.
- Decks are finite, legal for the opponent's resources and supported native effects, with bounded copies and tier-specific roles. Higher post-cap floors vary strategy/composition; they cannot produce infinitely increasing difficulty from a finite capped roster.
- Newly granted spells generate notifications but do not alter equipped decks. Existing high-level wizards receive missing primary-school grants on entry; eligible secondary-school choices appear in the Tome.

### Tickets and equipment tiers

Reward band B = 1 + floor((min(logical floor, native cap) - 1) / 10).

- Repeat victory: 3 × B Blue Arena Tickets.
- First-ever victory: total 10 × B Tickets (repeat amount plus 7 × B first-clear bonus).
- Post-cap rewards remain in the cap band. No arbitrary growth beyond capped enemy stats.
- Item target tiers correspond to levels 1, 10, 20, 30, ... and the exact native cap if it is not a multiple of ten.
- Tier at target level L requires character level at least L and permanent highest cleared floor at least L - 1. Initial level-1 tier needs no clear. Reset never removes earned eligibility.
- Buying tier B costs 10 × B Tickets per item. Buy one sequential tier at a time; never charge for a tier with no effective improvement. This makes the next affordable item upgrade roughly one current-band first clear, while a full eight-slot upgrade takes several clears or replays.
- A strong native item can skip target tiers that add no stats without payment, subject to level/clear eligibility. Its first purchased tier is the next eligible target that improves it; a purchase receipt records the destination tier.
- Numeric targets are school/slot profiles calibrated against native modest equipment. Distribute health, school damage/accuracy, resistance, mana and appropriate healing bonuses by slot rather than granting every stat on every item. Preserve school identities and cap totals. Targets must be exposed as versioned tables and previewed as exact before/after item and aggregate equipped-stat deltas before purchase.
- Upgrade delta for each approved stat = max(0, target - native item contribution), recomputed consistently without stacking prior upgrade deltas again. Preserve original item properties and stronger existing contributions. Jewel contributions remain separate and unchanged.
- If native constraints make a requested property unsupported, fail closed and report it instead of charging Tickets for an ineffective upgrade.

## Tome flow

- Status: current floor, permanent highest clear, native Ticket balance and next milestone.
- Spells: learned/unlocked primary spells and upcoming milestones; eligible secondary choices with prerequisite explanations, free confirmation and learned status.
- Equipment: equipped supported slots, item identity and upgrade school, current/next target tier, eligibility, exact stat preview and Blue Arena Ticket cost; explicit purchase confirmation.
- Reset Tower: confirm floor reset while preserving permanent history, wallet, spells and gear.
- No custom Ticket-spell purchase path remains active.

## Persistence, migration and safeguards

- Preserve existing Raven fencing, authority epochs, encounter admission, durable award/purchase receipts and recovery safeguards.
- Native wallet reward/spend and associated receipts must commit atomically or through a durable idempotent recovery protocol if the native storage boundary requires it. Other native currency writers cannot overwrite a newer balance from stale state.
- One-time custom balance migration must serialize with awards/spending, record its amount and completion, and be retry-safe. Never overwrite existing native Tickets. Wallet capacity/overflow must be handled without losing value or silently clipping; any such native limit discovered is a design follow-up before migration.
- Item upgrade persistence must identify the instance and school, hydrate on relog, recalculate on equip/unequip and avoid duplicate application. Reject ownership/equipment/expected-tier changes between preview and purchase; refresh the preview.
- Existing legacy damage-only upgrades must be migrated without another charge or duplicate bonuses. Preserve already-purchased value and document its mapping to new targets before deployment; unsupported legacy mappings require review rather than data loss.
- Replace growing clear arrays with compact durable first-clear history and receipts. Support sparse out-of-order clears and retained history across Reset. Do not infer unseen clears from highest-floor alone.

## Verification and deployment

- Regressions: enemy planning input permissions and native UI flags; label serialization; school health samples; exact spell ID eligibility and rejected low-level Mass Feint; automatic grants/idempotency; secondary prerequisites; native wallet migration/rewards/spending/concurrency/relog; item previews/effects/equip switching/relog; permanent first clears and reset.
- Full Release solution plus Tower and affected ordinary-game regressions before small coherent commits and isolated deployment.
- Fresh stopped-server saves and hash-verified rollback server backup, private Tower configuration and observed server readiness.
- User client acceptance remains necessary for gray/nonselectable cards, discard/Draw, labels, spell notifications/Tome choices, Blue Ticket display and gear effects. Source tests and startup are not client acceptance.

## Review limitations

The exact spell catalog and school/slot stat tables require an installed-content audit. The rules above define how to admit and balance them; no claim is made that those tables are already implemented or verified. Proposed numerical rules are an initial balance checkpoint and may need adjustment after representative manual fights.
