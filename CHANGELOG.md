# Changelog

All notable changes to Sounds of Nature are documented here.

## 0.8.3 — 2026-09-30

### Added
- **Per-player settings file** at `VintagestoryData/ModConfig/soundsofnature.json`.
  It lists every sound group by id and label, and gives each one three knobs:
  `enabled`, `volume` (multiplier) and `spacing` (how much rarer to play it).
  Unlike `soundgroups.json` inside the mod zip, this file is yours — it is never
  overwritten by a mod update, and new groups are added to it automatically.
- New commands so nobody has to open a file at all:
  - `.son groups` — every group id, label, and your current settings for it
  - `.son mute <id>` / `.son unmute <id>` — silence one group (`all` also works)
  - `.son gvol <id> <n>` — volume multiplier for one group, e.g. `0.3`
  - `.son gspace <id> <n>` — play one group n times less often, e.g. `3`
  - `.son space <n>` — space out *every* sound by n
  - `.son reset <id>` / `.son reset all` — back to the mod defaults
  - `.son config` — where the settings file is and what's overridden
- `.son on`, `.son off` and `.son vol` now persist across restarts, and
  `.son reload` re-reads the settings file as well as `soundgroups.json`.

### Changed
- Cicadas toned down after player feedback that they were piercing and
  incessant: quieter (max volume 0.119 → 0.085), less frequent (interval
  20–58s → 45–120s), and no longer able to overlap themselves
  (`maxConcurrent` 2 → 1).

## 0.8.2 — 2026-09-21

### Added
- Underwater awareness: when your head goes below the surface, wildlife falls
  silent — no new sounds start, and anything already playing fades out within a
  fraction of a second, then fades back in when you surface. Wading, swimming at
  the surface, and standing in shallow or flowing water sound normal.
- Temporal storm sounds stay faintly audible underwater (about a third volume).
- `.son status` shows "UNDERWATER" and drops wildlife from the eligible list
  while submerged.

## 0.8.1 — 2026-09-20

### Fixed
- The `rainfall` condition was matched against live precipitation instead of the
  biome's wetness (the weather system overwrites `Rainfall` on a `NowValues`
  climate read; the mod now uses `WorldgenRainfall`). Thirteen wet-biome groups
  could never play at all — loons, alligators, macaws, howler monkeys, the frog
  and cricket groups, cicadas, bees, elk bugles — and the dry-biome groups
  (hyenas, lions, weaver birds, rattlesnakes, coyotes, goats) were playing in
  jungles and swamps. Both now behave as their conditions describe.
- `.son status` shows biome wetness under "Rain" and live rain under "Precip".

## 0.8.0 — 2026-07-25

- Temporal storms now have their own horror soundscape: low growls, distant
  guttural chatter, grinding stone, vast bellowing roars, and unsettling
  whispers play while a storm rages.
- Normal wildlife falls silent during a temporal storm; the storm groups take
  over, and they play everywhere — outdoors, indoors, and deep underground —
  unaffected by rain or wind mutes.
- New `duringTemporalStorm` flag available in `soundgroups.json`.
- `.son status` now reports whether a temporal storm is active.
- Also verified against Vintage Story 1.22.7 and rebuilt against its assemblies.

## 0.7.4 — 2026-07-25

- Packaging release: the mod zip is now named with its version number
  (`SoundsOfNature-0.7.4.zip`) as required for ModDB uploads. No code changes.

## 0.7.3 — 2026-07-23

- Compatibility release for Vintage Story 1.22.5. Rebuilt against the 1.22.5
  assemblies; no code changes.

## [0.7.1]

### Fixed
- Forest density was always read as 0.00, on every world (thanks to the player
  report from a TerraPrety world that surfaced this — it was not a worldgen-mod
  incompatibility, and Sounds of Nature remains fully compatible with TerraPrety
  and similar worldgen mods). The game only fills in forest density on the
  server side; on the client it is always 0, so the mod now samples the synced
  worldgen forest map directly.
  - Forest-dwelling groups that never played now do: alligators, macaws,
    howler monkeys, cicadas, elk bugles, and chickadees.
  - Open-terrain groups (hyenas, lions, weaver birds, rattlesnakes, coyotes,
    goats, loons, bees, lake ice) no longer incorrectly play inside dense
    forest.
  - `.son status` now reports the real forest density.

## [0.7.0]

### Added
- Seagulls: a daytime coastal group that only plays near the ocean. New
  `requiresOcean` sound-group condition — the mod samples the fluid layer at
  sea level around the player for saltwater (48-block radius, cached ~10s) so
  gulls fire on beaches, coastal cliffs, and at sea, but never inland.
- `.son status` now reports whether the ocean is nearby.

## [0.6.0]

### Added
- New wildlife: chickadees (cold forests, daytime), ravens (deep winter),
  elk bugles (autumn rut, dawn/dusk/night), howler monkeys (jungle),
  coyote yips (arid scrubland nights), and cracking lake ice (open winter
  landscapes below -2°C).

## [0.5.0]

### Added
- New biome wildlife: rattlesnakes (rare, desert), lions (distant, savannah),
  alligators (jungle), macaws (jungle, daytime), weaver birds (savannah/desert,
  daytime), and a dusk/night cricket ambience for every non-desert biome.
- More variety for existing groups: extra temperate songbird and perched-crow
  calls.
- Frog croak chorus: a new dusk/night pond group in spring/summer.
- Overlapping-voice "burst" support: a sound group can fire several voices per
  trigger via a `burst` { min, max } config field, each with its own randomized
  volume and position so some croaks read louder/nearer than others. Capped by
  the group's maxConcurrent; groups without `burst` still play a single voice.

## [0.4.0]

### Changed
- Sounds are now placed at real world coordinates so OpenAL applies true 3D
  spatial attenuation, instead of being emitted at the player. Ambient sounds
  scatter to a randomized direction and distance (15–50 blocks away), and
  particle- or entity-driven groups anchor to the actual source position.
