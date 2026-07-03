# Changelog

All notable changes to Sounds of Nature are documented here.

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
