# Infinite Golem Tower: local test checklist

This checklist is for the ten-floor Adventure-mode slice on a local test server. The feature is opt-in and remains disabled by default.

## Build and automated checks

From the repository root, build the full solution:

```powershell
dotnet build src/Imlight.sln --configuration Release -m:1
```

Run the focused integration executable (the RavenDB test modes expect the local test server at `http://127.0.0.1:8180`):

```powershell
dotnet run --project tests/Imlight.Customization.Tests/Imlight.Customization.Tests.csproj -c Release -- --tower-run
dotnet run --project tests/Imlight.Customization.Tests/Imlight.Customization.Tests.csproj -c Release -- --tower-persistence
dotnet run --project tests/Imlight.Customization.Tests/Imlight.Customization.Tests.csproj -c Release -- --tower-awards
dotnet run --project tests/Imlight.Customization.Tests/Imlight.Customization.Tests.csproj -c Release -- --tower-actor
dotnet run --project tests/Imlight.Customization.Tests/Imlight.Customization.Tests.csproj -c Release -- --tower-duel
dotnet run --project tests/Imlight.Customization.Tests/Imlight.Customization.Tests.csproj -c Release
```

The server loads `[Tower]` from `Imlight.ini`. Defaults are `Enabled = false`, `ExperienceBase = 25`, `ExperiencePerFloor = 10`, `FloorFiveRewardTemplateId = 87256`, and `FloorTenRewardTemplateId = 126983`.

## Local client/server smoke test

Install the Release output from `src/Imlight.Director/bin/Release/net10.0/` using the server's normal local deployment process, keeping its usual content files and `Imlight.ini` beside the executable. Use a disposable server/RavenDB database and test wizard. First launch once with `Enabled = false` and confirm the existing tower behavior is unchanged. Then set `Enabled = true` in that deployment's `Imlight.ini`, restart the server, and enter through the existing Golem Tower entrance.

- [ ] Entry creates or resumes one private run for the wizard and starts at logical floor 1 in physical room 1.
- [ ] Clear floors 1–5. Confirm the floor and encounter advance after each committed victory; floors 1 and 6 use the same physical room, and the actor/zone is recreated for the next encounter.
- [ ] Confirm the generated encounter contains at most two enemies and uses the seeded manifest's archetype, school, level, and spell profile. Try a summon and confirm it is refused in Tower combat.
- [ ] Confirm ordinary combat outside Tower still awards its usual XP and drops.
- [ ] Confirm Tower combat grants its configured floor XP once, suppresses native duplicate XP/drops, and persists wizard XP/level and inventory changes after relogging.
- [ ] On floor 5, verify the configured milestone item and the highest-cleared-floor progression. Continue through floors 6–10 and confirm the second boss configuration is used on floor 10.
- [ ] On floor 10, verify the configured milestone item, progression, and successful run completion/exit.
- [ ] Leave voluntarily during floor 3 and re-enter; confirm the incomplete attempt is discarded and the run restarts from floor 1. Repeat after clearing floor 5; confirm re-entry starts at floor 6.
- [ ] Defeat an encounter and confirm the run returns to its saved checkpoint. If a character dies on the same combat resolution as the last enemy, confirm the outcome is defeat.
- [ ] In a fresh run, note a wizard's HP and mana at floor entry, then disconnect during combat. Confirm there is no native flee or mana drain, the duel ends without a win/loss or reward, and the room restarts at the same logical floor with the saved HP and mana. This uses the run's single recovery replay; floor clears and checkpoint restarts do not replenish it.
- [ ] In another fresh run, stop the server after a manifest is saved but before combat starts. Restart and confirm the ready encounter resumes without spending the replay.
- [ ] Stop the server during active combat. After the 20-second ownership lease expires, confirm the new process takes over, rebuilds the same logical floor, restores the floor-entry HP/mana snapshot, and fences the prior authority. The 5-second heartbeat keeps a live process lease current; a graceful shutdown releases it immediately.
- [ ] After the replayed encounter has entered combat, interrupt it again with the replay already spent. Confirm the run fails closed to the next checkpoint attempt and grants no Tower reward.
- [ ] For interruption recovery, stop the server after a floor clear and before the recipient receives its pending award. Restart and re-enter; confirm the outbox replays the award once and the receipt prevents duplication.
- [ ] Repeat entry after a clean server restart and confirm the persisted floor, manifest, and attempt do not change unexpectedly.

Record the server log around entry, each floor transfer, reward delivery, defeat/restart, and floor 10 completion. Structured log messages include `Golem Tower run`, `creates floor`, and `Tower award`. In-game checks require the local Wizard101 client and server; they were not executable in this build environment.
