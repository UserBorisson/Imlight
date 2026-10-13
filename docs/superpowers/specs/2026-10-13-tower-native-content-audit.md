# Tower native content audit ? 2026-10-13

Source: installed `r806919.Wizard_1_610` Root.wad and generated native object/protocol schema. Archive inspection was read-only (173,152 paths, 18,173 decoded spell templates).

## Native transport

`MSG_SHOWCOMBATUI` has DuelID, AltTurn, AltTurnTeam, DynamicTurnFlags and DynamicSubcircles. Existing Tower messages omitted the alternate-turn fields. The corrected payload sends the acting team and alternate-turn counter; participant `m_myTeamTurn` follows the same team. Ordinary duel payloads are preserved. Gray cards, targeting lock, discard and Draw still require user client acceptance.

Native player name behavior supports `m_useRank`; school behavior level is signed Int32. Tower transmits logical floor there, capped at Int32.MaxValue, while authoritative combat level remains capped. School icon/Level formatting require user client acceptance.

## Native currency

CURRENCY_PVPCUR=5 maps to PvPCurrency.xml and `m_currentPvPCurrency`, with `MSG_UPDATEPVPCURRENCY(PvPCurrency, MaxPvPCurrency)`. Legacy CURRENCY_ARENATICKETS=1/`m_currentArenaPoints` and event currency 3/`m_currentEventCurrency1` are separate. The new wallet uses the PvP currency; the actual Blue counter is pending manual acceptance. Signed Int32 is the transport/storage ceiling; checked arithmetic rejects overflow without consuming migration or award receipts. No clipping is permitted. The update advertises that storage ceiling; no smaller installed server wallet capacity is configured.

Wallet writers audited: Tower awards, Tome purchases, character stat persistence. Ordinary shops use Gold. Stale ordinary stat saves retain the authoritative persisted PvP currency and retry optimistic conflicts.

Legacy custom remaining balance converts once, 1:1, in the same transaction as its migration marker. Already spent Tickets are not restored. Contiguous clear history becomes merged intervals; sparse gaps remain gaps. Reset preserves permanent history.

## Spell policy

The table below names exact installed learned variants. Enemy access uses this catalog plus supported native effects/costs, independently of misleading trainer metadata. Mass Feint 203556948 is excluded. Primary native prerequisites are waived; secondary prerequisites remain. Explicit utility levels are policy-reviewed overrides of incomplete metadata. Major attacks use conservative maximum native/trainer level. Clear milestones 3/5/8 are alternate player unlocks; enemies access them only on a later floor unless already available at the regular level.

Similarly named mob, item, Treasure or Shadow variants are not substitutes for canonical learned IDs. The catalog covers the seven primary schools and reviewed Sun/Star training. Localized names resolved internal aliases including Winters Web (Abominable Weaver), Defibrillate (Healing Current), Blood Moon (Time of Legend), Colossus Mantis (Mystic Colossus), Katzenstein's Monster and Voodoo (Bad Juju). Moon/Shadow expansion still requires explicit exact-ID review.

## Exact learned-spell catalog

| School | Display name | Template ID | Level | Clear | Secondary | Prerequisite | Focus restriction |
| --- | --- | ---: | ---: | ---: | --- | ---: | --- |
| Balance | Scarab | 2143810477 | 1 | 0 | True | 0 |  |
| Balance | Black Mantle | 1730822561 | 3 | 0 | False | 0 |  |
| Balance | Elemental Shield | 751181746 | 5 | 0 | False | 0 |  |
| Balance | Scorpion | 1462253104 | 5 | 0 | True | 2143810477 |  |
| Balance | Weakness | 1321504283 | 8 | 8 | True | 1462253104 |  |
| Balance | Locust Swarm | 1965296812 | 10 | 0 | True | 1321504283 |  |
| Balance | Precision | 491728358 | 10 | 0 | False | 0 |  |
| Balance | Spirit Shield | 878360657 | 10 | 0 | False | 0 |  |
| Balance | Balanceblade | 514206205 | 12 | 3 | False | 0 |  |
| Balance | Helping Hands | 1267282076 | 18 | 0 | False | 0 |  |
| Balance | Hex | 92296 | 18 | 5 | False | 0 |  |
| Balance | Donate Power | 1484841148 | 20 | 0 | True | 254485266 |  |
| Balance | Gearhead Destroyer | 767057080 | 20 | 0 | True | 1829994384 |  |
| Balance | Reshuffle | 158663651 | 20 | 0 | True | 0 |  |
| Balance | Sandstorm | 1829994384 | 20 | 0 | True | 1965296812 |  |
| Balance | Balance of Power | 84457148 | 22 | 0 | False | 767057080 |  |
| Balance | Balance of Power | 717993721 | 22 | 0 | True | 767057080 |  |
| Balance | Unbalance | 115459838 | 22 | 0 | True | 0 |  |
| Balance | Elemental Blade | 635344572 | 25 | 0 | True | 0 |  |
| Balance | Elemental Trap | 415159068 | 25 | 0 | True | 0 |  |
| Balance | Spirit Blade | 1893711069 | 25 | 0 | True | 0 |  |
| Balance | Spirit Trap | 1376756333 | 25 | 0 | True | 0 |  |
| Balance | Judgement | 562901781 | 28 | 0 | False | 0 |  |
| Balance | Spectral Blast | 254485266 | 30 | 0 | True | 717993721 |  |
| Balance | Hydra | 70785800 | 50 | 0 | True | 1484841148 |  |
| Balance | Power Nova | 2062835293 | 50 | 0 | False | 0 |  |
| Balance | Availing Hands | 1894026344 | 55 | 0 | False | 0 |  |
| Balance | Ra | 2066 | 60 | 0 | False | 0 |  |
| Balance | Chimera | 529027789 | 70 | 0 | False | 0 |  |
| Balance | Supernova | 1493403952 | 76 | 0 | False | 0 |  |
| Balance | Sabertooth | 1829426808 | 90 | 0 | False | 0 |  |
| Balance | Gaze of Fate | 1039918614 | 100 | 0 | False | 0 |  |
| Balance | Nested Fury | 1927982914 | 110 | 0 | False | 0 |  |
| Balance | Sand Wurm | 1373405296 | 120 | 0 | False | 0 |  |
| Balance | Scion of Balance | 205203928 | 130 | 0 | False | 0 |  |
| Death | Dark Sprite | 1731857280 | 1 | 0 | True | 0 |  |
| Death | Ghoul | 82556199 | 5 | 0 | True | 1731857280 |  |
| Death | Death Prism | 93660952 | 7 | 0 | False | 0 |  |
| Death | Deathblade | 2136741217 | 7 | 3 | False | 0 |  |
| Death | Dream Shield | 406227672 | 8 | 8 | True | 82556199 |  |
| Death | Banshee | 827805677 | 10 | 0 | True | 406227672 |  |
| Death | Death Shield | 409737272 | 10 | 0 | True | 0 |  |
| Death | Infection | 334242429 | 10 | 0 | True | 0 |  |
| Death | Death Trap | 77680409 | 16 | 5 | True | 0 |  |
| Death | Plague | 1888318032 | 16 | 0 | False | 0 |  |
| Death | Curse | 75022979 | 18 | 0 | False | 0 |  |
| Death | Empower | 257345937 | 18 | 0 | False | 0 |  |
| Death | Sacrifice | 927448295 | 18 | 0 | False | 0 |  |
| Death | Strangle | 1967013752 | 22 | 0 | True | 0 |  |
| Death | Feint | 90582150 | 26 | 0 | True | 524731385 |  |
| Death | Animate | 331895312 | 28 | 0 | False | 0 |  |
| Death | Poison | 1730630192 | 28 | 0 | False | 0 |  |
| Death | Vampire | 524731385 | 30 | 0 | True | 827805677 |  |
| Death | Doom and Gloom | 728541335 | 33 | 0 | True | 732623153 |  |
| Death | Skeletal Pirate | 732623153 | 40 | 0 | True | 90582150 |  |
| Death | Virulent Plague | 1599984203 | 40 | 0 | False | 0 |  |
| Death | Scarecrow | 41441599 | 50 | 0 | False | 0 |  |
| Death | Wraith | 1788506505 | 50 | 0 | False | 0 |  |
| Death | Skeletal Dragon | 1111782343 | 60 | 0 | False | 0 |  |
| Death | Beguile | 591686509 | 65 | 0 | False | 0 |  |
| Death | Dr. Von's Monster | 1327412743 | 68 | 0 | False | 0 |  |
| Death | Bad Juju | 1697434154 | 72 | 0 | False | 0 |  |
| Death | Avenging Fossil | 250531677 | 90 | 0 | False | 0 |  |
| Death | Call of Khrulhu | 169474362 | 100 | 0 | False | 0 |  |
| Death | Winged Sorrow | 625950838 | 110 | 0 | False | 0 |  |
| Death | Qismah's Curse | 754271745 | 120 | 0 | False | 0 |  |
| Death | Scion of Death | 1521078555 | 130 | 0 | False | 0 |  |
| Fire | Fire Cat | 103007158 | 1 | 0 | True | 0 |  |
| Fire | Fireblade | 603748810 | 2 | 3 | False | 0 |  |
| Fire | Fire Elf | 1243857701 | 5 | 0 | True | 103007158 |  |
| Fire | Fire Prism | 534106022 | 7 | 0 | False | 0 |  |
| Fire | Glacial Shield | 1696448056 | 8 | 8 | True | 1243857701 |  |
| Fire | Fire Shield | 1731855964 | 10 | 0 | True | 0 |  |
| Fire | Sunbird | 1599065470 | 10 | 0 | True | 1696448056 |  |
| Fire | Link | 2404620 | 12 | 0 | False | 0 |  |
| Fire | Steal Charm | 975200773 | 15 | 0 | False | 0 |  |
| Fire | Fire Trap | 400321766 | 16 | 5 | True | 1599065470 |  |
| Fire | Heck Hound | 1876146125 | 18 | 0 | False | 0 |  |
| Fire | Smoke Screen | 496482937 | 18 | 0 | False | 0 |  |
| Fire | Quench | 1810489711 | 22 | 0 | True | 0 |  |
| Fire | Immolate | 2031697996 | 28 | 0 | True | 400321766 |  |
| Fire | Meteor Strike | 729953007 | 30 | 0 | True | 400321766 |  |
| Fire | Wyldfire | 373504299 | 30 | 0 | True | 729953007 |  |
| Fire | Fuel | 2563718 | 35 | 0 | True | 400321766 |  |
| Fire | A-baahh-calypse | 745202301 | 40 | 0 | False | 0 |  |
| Fire | Phoenix | 1765559002 | 40 | 0 | True | 373504299 |  |
| Fire | Fire Dragon | 921541606 | 50 | 0 | False | 0 |  |
| Fire | Helephant | 719065368 | 50 | 0 | True | 1765559002 |  |
| Fire | Power Link | 84605331 | 55 | 0 | False | 0 |  |
| Fire | Efreet | 1401700123 | 60 | 0 | False | 0 |  |
| Fire | Rain of Fire | 1038559747 | 70 | 0 | False | 0 |  |
| Fire | Backdraft | 530263518 | 72 | 0 | False | 0 |  |
| Fire | Detonate | 2033788231 | 72 | 0 | False | 603748810 |  |
| Fire | Sun Serpent | 141079815 | 90 | 0 | False | 0 |  |
| Fire | Fire from Above | 576217214 | 100 | 0 | False | 0 |  |
| Fire | Raging Bull | 1946402814 | 110 | 0 | False | 0 |  |
| Fire | Scorching Scimitars | 592954350 | 120 | 0 | False | 0 |  |
| Fire | Scion of Fire | 1125749132 | 130 | 0 | False | 0 |  |
| Fire | Phantasmania! | 1724616268 | 170 | 0 | False | 0 |  |
| Ice | Frost Beetle | 1496157882 | 1 | 0 | True | 0 |  |
| Ice | Stun Block | 1143963608 | 1 | 0 | True | 0 |  |
| Ice | Freeze | 1887199642 | 2 | 0 | False | 0 |  |
| Ice | Snow Serpent | 219826034 | 5 | 0 | True | 1496157882 |  |
| Ice | Ice Prism | 419343421 | 7 | 0 | False | 0 |  |
| Ice | Volcanic Shield | 1382626752 | 8 | 0 | True | 219826034 |  |
| Ice | Evil Snowman | 1070933106 | 10 | 0 | True | 1382626752 |  |
| Ice | Snow Shield | 1731424937 | 10 | 0 | True | 0 |  |
| Ice | Steal Ward | 991342922 | 15 | 0 | False | 0 |  |
| Ice | Ice Trap | 415162663 | 16 | 5 | True | 0 |  |
| Ice | Tower Shield | 432324968 | 16 | 8 | True | 1070933106 |  |
| Ice | Melt | 2832525 | 22 | 0 | True | 0 |  |
| Ice | Balefrost | 1615064713 | 30 | 0 | True | 126627734 |  |
| Ice | Blizzard | 126627734 | 30 | 0 | True | 829983860 |  |
| Ice | Ice Wyvern | 829983860 | 30 | 0 | True | 432324968 |  |
| Ice | Legion Shield | 3605232 | 35 | 0 | False | 0 |  |
| Ice | Frostbite | 1050086956 | 38 | 0 | False | 0 |  |
| Ice | Iceblade | 2032132496 | 38 | 3 | False | 0 |  |
| Ice | Colossus | 485049522 | 50 | 0 | False | 44462310 |  |
| Ice | Frost Giant | 1003408089 | 50 | 0 | False | 0 |  |
| Ice | Snow Angel | 1037864232 | 60 | 0 | False | 0 |  |
| Ice | Woolly Mammoth | 799353558 | 70 | 0 | False | 0 |  |
| Ice | Lord of Winter | 1695700605 | 90 | 0 | False | 0 |  |
| Ice | Abominable Weaver | 1635493379 | 100 | 0 | False | 0 |  |
| Ice | Snowball Barrage | 1912025401 | 110 | 0 | False | 0 |  |
| Ice | Climaclysm | 2109842312 | 120 | 0 | False | 0 |  |
| Ice | Scion of Ice | 414216592 | 130 | 0 | False | 0 |  |
| Life | Imp | 84361 | 1 | 0 | True | 0 |  |
| Life | Fairy | 75304275 | 2 | 0 | False | 0 |  |
| Life | Pixie | 74836240 | 2 | 0 | False | 0 |  |
| Life | Leprechaun | 2106466410 | 5 | 0 | True | 84361 |  |
| Life | Life Prism | 534093744 | 7 | 0 | False | 0 |  |
| Life | Legend Shield | 337707760 | 8 | 8 | True | 2106466410 |  |
| Life | Life Shield | 1731876438 | 10 | 0 | True | 0 |  |
| Life | Life Trap | 400309488 | 10 | 5 | True | 0 |  |
| Life | Sprite | 1889156557 | 10 | 0 | True | 337707760 |  |
| Life | Nature's Wrath | 784677378 | 12 | 0 | False | 0 |  |
| Life | Spirit Armor | 2071077573 | 16 | 0 | True | 1889156557 |  |
| Life | Legend Golem | 527280456 | 20 | 0 | True | 2071077573 |  |
| Life | Unicorn | 550546918 | 20 | 0 | False | 0 |  |
| Life | Lifeblade | 603728324 | 21 | 3 | False | 0 |  |
| Life | Entangle | 1967006754 | 22 | 0 | True | 0 |  |
| Life | Guiding Light | 1034298003 | 25 | 0 | True | 84793363 |  |
| Life | Satyr | 84793363 | 30 | 0 | True | 1792950125 |  |
| Life | Seraph | 1792950125 | 30 | 0 | True | 527280456 |  |
| Life | Regenerate | 248210062 | 32 | 0 | False | 1437424064 |  |
| Life | Earth Walker | 323588608 | 33 | 0 | True | 84793363 |  |
| Life | Sanctuary | 278860909 | 33 | 0 | True | 775630999 |  |
| Life | Dryad | 73526884 | 38 | 0 | False | 0 |  |
| Life | Centaur | 775630999 | 40 | 0 | True | 323588608 |  |
| Life | Forest Lord | 1408643162 | 50 | 0 | False | 0 |  |
| Life | Rebirth | 1392148352 | 60 | 0 | False | 0 |  |
| Life | Gnomes! | 1569212953 | 70 | 0 | False | 0 |  |
| Life | Spinysaur | 474579317 | 90 | 0 | False | 0 |  |
| Life | Hungry Caterpillar | 1793130233 | 100 | 0 | False | 0 |  |
| Life | Wings of Fate | 1106087120 | 110 | 0 | False | 0 |  |
| Life | Lamassu | 499675719 | 120 | 0 | False | 0 |  |
| Life | Scion of Life | 1226363276 | 130 | 0 | False | 0 |  |
| Myth | Blood Bat | 1067010286 | 1 | 0 | True | 0 |  |
| Myth | Stun | 2514611 | 5 | 0 | False | 0 |  |
| Myth | Troll | 82261620 | 5 | 0 | True | 1067010286 |  |
| Myth | Myth Trap | 400226031 | 8 | 5 | True | 82261620 |  |
| Myth | Cyclops | 1691813655 | 10 | 0 | True | 400226031 |  |
| Myth | Myth Shield | 1732025431 | 10 | 0 | True | 0 |  |
| Myth | Pierce | 1911022320 | 10 | 0 | False | 0 |  |
| Myth | Cleanse Ward | 1048474958 | 12 | 0 | False | 0 |  |
| Myth | Mythblade | 603644867 | 12 | 3 | False | 0 |  |
| Myth | Ether Shield | 432324119 | 16 | 8 | True | 1691813655 |  |
| Myth | Vaporize | 386320936 | 22 | 0 | True | 0 |  |
| Myth | Time of Legend | 1148139729 | 26 | 0 | True | 1272123051 |  |
| Myth | Humongofrog | 1272123051 | 30 | 0 | True | 432324119 |  |
| Myth | Minotaur | 949570680 | 40 | 0 | False | 1148139729 |  |
| Myth | Earthquake | 425481960 | 50 | 0 | False | 633396160 |  |
| Myth | Orthrus | 1862621819 | 50 | 0 | False | 0 |  |
| Myth | Shatter | 258673959 | 50 | 0 | False | 0 |  |
| Myth | Medusa | 2028234611 | 60 | 0 | False | 0 |  |
| Myth | Basilisk | 1457899086 | 70 | 0 | False | 0 |  |
| Myth | Celestial Calendar | 119326103 | 90 | 0 | False | 0 |  |
| Myth | Mystic Colossus | 2062086375 | 100 | 0 | False | 0 |  |
| Myth | Witch's House Call | 1305255487 | 110 | 0 | False | 0 |  |
| Myth | Snake Charmer | 943026498 | 120 | 0 | False | 0 |  |
| Myth | Scion of Myth | 1873219188 | 130 | 0 | False | 0 |  |
| Star | Fortify | 927252016 | 50 | 0 | True | 0 |  |
| Star | Amplify | 927512169 | 52 | 0 | True | 0 |  |
| Star | Vengeance | 108197789 | 56 | 0 | True | 0 |  |
| Star | Empowerment | 235884281 | 58 | 0 | True | 0 |  |
| Star | Berserk | 1618526352 | 66 | 0 | True | 0 |  |
| Star | Mend | 2306189 | 66 | 0 | True | 0 |  |
| Star | Chastisement | 2086167071 | 84 | 0 | True | 0 | Balance |
| Star | Cycle of Life | 1235843804 | 84 | 0 | True | 0 | Life |
| Star | Devotion | 1466356455 | 84 | 0 | True | 0 | Life |
| Star | Furnace | 1036631401 | 84 | 0 | True | 0 | Fire |
| Star | Galvanic Field | 1000012417 | 84 | 0 | True | 0 | Storm |
| Star | Punishment | 719356245 | 84 | 0 | True | 0 | Balance |
| Star | Reliquary | 281681134 | 84 | 0 | True | 0 | Myth |
| Star | Sleet Storm | 1000501217 | 84 | 0 | True | 0 | Ice |
| Star | Virulence | 250316317 | 84 | 0 | True | 0 | Death |
| Star | Adapt | 90770593 | 110 | 0 | True | 235884281 |  |
| Star | Brace | 74485346 | 110 | 0 | True | 927252016 |  |
| Star | Frenzy | 1215947162 | 110 | 0 | True | 1618526352 |  |
| Star | Magnify | 927591397 | 110 | 0 | True | 927512169 | Balance |
| Storm | Thunder Snake | 2062265892 | 1 | 0 | True | 0 |  |
| Storm | Lightning Bats | 1184018227 | 5 | 0 | True | 2062265892 |  |
| Storm | Storm Prism | 96869679 | 7 | 0 | False | 0 |  |
| Storm | Thermic Shield | 1252708696 | 8 | 8 | True | 1184018227 |  |
| Storm | Storm Shark | 975292929 | 10 | 0 | True | 1252708696 |  |
| Storm | Storm Shield | 406790657 | 10 | 0 | True | 0 |  |
| Storm | Cleanse Charm | 449625782 | 12 | 0 | False | 0 |  |
| Storm | Stormblade | 2131701592 | 12 | 3 | False | 0 |  |
| Storm | Wild Bolt | 2078052508 | 12 | 0 | False | 0 |  |
| Storm | Storm Trap | 82998574 | 16 | 5 | True | 975292929 |  |
| Storm | Dissipate | 457669964 | 22 | 0 | True | 0 |  |
| Storm | Tempest | 1569112959 | 28 | 0 | False | 0 |  |
| Storm | Darkwind | 391430711 | 30 | 0 | True | 1829994132 |  |
| Storm | Kraken | 1737191829 | 30 | 0 | True | 82998574 |  |
| Storm | Windstorm | 1829994132 | 30 | 0 | True | 1737191829 |  |
| Storm | Disarm | 1627273980 | 35 | 0 | False | 0 |  |
| Storm | Stormzilla | 252684095 | 40 | 0 | True | 391430711 |  |
| Storm | Storm Lord | 82896576 | 50 | 0 | False | 0 |  |
| Storm | Triton | 1730466188 | 50 | 0 | True | 252684095 |  |
| Storm | Insane Bolt | 1885860871 | 55 | 0 | False | 0 |  |
| Storm | Leviathan | 1405772650 | 60 | 0 | False | 0 |  |
| Storm | Sirens | 1564229357 | 70 | 0 | False | 0 |  |
| Storm | Healing Current | 1008861450 | 75 | 0 | False | 0 |  |
| Storm | Storm Owl | 990349312 | 90 | 0 | False | 0 |  |
| Storm | Glowbug Squall | 1881888814 | 100 | 0 | False | 0 |  |
| Storm | Rusalka's Wrath | 348289258 | 110 | 0 | False | 0 |  |
| Storm | Iron Sultan | 655769628 | 120 | 0 | False | 0 |  |
| Storm | Scion of Storm | 1039085841 | 130 | 0 | False | 0 |  |
| Sun | Keen Eyes | 170282421 | 50 | 0 | True | 0 |  |
| Sun | Strong | 1966685517 | 50 | 0 | True | 0 |  |
| Sun | Accurate | 2029287812 | 52 | 0 | True | 170282421 |  |
| Sun | Giant | 90574087 | 52 | 0 | True | 1966685517 |  |
| Sun | Sniper | 1602671117 | 54 | 0 | True | 2029287812 |  |
| Sun | Monstrous | 1634263158 | 56 | 0 | True | 90574087 |  |
| Sun | Gargantuan | 1740946502 | 58 | 0 | True | 1634263158 |  |
| Sun | Unstoppable | 1513716247 | 58 | 0 | True | 1602671117 |  |
| Sun | Colossal | 485049421 | 64 | 0 | True | 1740946502 |  |
| Sun | Potent Trap | 1383355792 | 86 | 0 | True | 0 |  |
| Sun | Primordial | 1596035799 | 86 | 0 | True | 0 |  |
| Sun | Sharpened Blade | 966810308 | 86 | 0 | True | 0 |  |
| Sun | Aegis | 85300353 | 110 | 0 | True | 0 |  |
| Sun | Epic | 2141733 | 110 | 0 | True | 485049421 |  |
| Sun | Indemnity | 655113637 | 110 | 0 | True | 0 |  |
| Sun | Radical | 2045405183 | 110 | 0 | True | 1596035799 |  |

## Equipment target profile (initial balance checkpoint)

The versioned resource `tower-item-upgrade-targets.json` copies the established seven-school starter budgets and allocates them to all eight combat slots. This is an initial balance profile, not a claim that native endgame gear was measured in client fights.

| School | HP | Mana | School damage % | Accuracy % | Universal resist % | Healing % |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Fire |125|30|18|10|8|0|
| Ice |200|25|14|8|11|0|
| Storm |100|35|20|11|7|0|
| Myth |140|30|17|10|9|0|
| Life |180|35|14|10|10|10|
| Death |160|30|16|10|9|0|
| Balance |165|35|16|10|10|0|

Multipliers at levels 1/10/30/50/100/170: HP 1/2/4/7/14/22; mana 1/1.25/2/3/5/7; damage 1/1.25/2/3/6/9; accuracy 1/1/1.2/1.5/2/2.5; resist 1/1.25/2/2.5/3.5/4; healing 1/1/2/3/5/7. Intermediate target levels interpolate; percentages round to native one-percent units. The installed cap is 170; configured lower caps stop earlier.

Slot allocations (HP/mana/damage/accuracy/resist/healing percent of total): Hat 20/0/25/30/30/0; Robe 30/0/25/30/40/30; Boots 15/0/20/25/30/0; Wand 0/0/15/15/0/0; Athame 10/25/5/0/0/30; Amulet 10/25/0/0/0/0; Ring 10/25/10/0/0/40; Deck 5/25/0/0/0/0.

Native effect encoding uses exact canonical effect names and inverse lookup into installed stat tables. Effects carry the item originator ID so cleanup distinguishes them from ordinary effects of the same slot/type. An unencodable preview is rejected before spending. Native cards, sockets, jewels and pip features are not modified.

Legacy paid universal damage remains permanent and counts toward the damage target. Legacy purchase receipts provide item-bound credit toward future target costs; each use is saved in the same atomic purchase transaction. No wallet refund/transfer or second charge for the old purchase occurs. New target tiers use versioned receipts.
