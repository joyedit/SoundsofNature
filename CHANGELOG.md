# Changelog

All notable changes to Sounds of Nature are documented here.

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
