# Sounds of Nature — Vintage Story Mod

> **Status: Active development.** This mod is playable and stable enough to release, but it is still being actively expanded and tuned. Expect new sounds, new creatures, and balance changes in future updates. Feedback is very welcome.

Brings the world to life with realistic ambient wildlife audio that reacts to where you are, what time it is, and what season you're in. As you explore, you'll hear distant songbirds at dawn in forested areas, crows calling through the midday heat, hyenas whooping across the desert at dusk, wolves howling on cold winter nights, frogs and crickets singing in warm wet evenings, bats and dripping water deep underground, and much more — all randomized in volume, timing, and position so no two moments sound the same.

---

## Client-Side Only

This mod runs **entirely on the client**. It does not need to be installed on a server, and the server does not need to know it exists. Each player installs it independently and hears their own ambient soundscape. It is safe to use on any multiplayer server.

---

## How It Works

Every 2 seconds the mod checks your current position and reads the world's climate data — temperature, rainfall, and forest density — along with the current time of day and season. It then compares those values against each configured sound group and plays sounds from any groups whose conditions are met. Sounds are placed at a randomized distance and direction around you (15–50 blocks away) to give the impression of wildlife nearby but out of sight. Volume is intentionally varied using a curve that biases most sounds toward the quieter end, with occasional closer-sounding calls to keep things feeling alive without being intrusive.

---

## Sound Groups

### Surface

| Group | Biome | Time | Season |
|---|---|---|---|
| Songbirds & Cardinals | Warm, some vegetation | Dawn – Afternoon | All |
| Mourning Doves | Mild, any biome | Dawn – Morning | All |
| Loons | Cool, wet, open water | Dawn, Morning, Dusk, Night | All |
| Crows & Ravens | Anywhere | Morning – Afternoon | All |
| Crows in Flight | Anywhere | Dawn – Dusk | All |
| Owls | Cool, forested | Dusk – Night | All |
| Barn Owl Screeches | Temperate, any biome | Dusk – Night | All |
| Wolves | Cold, forested | Dusk – Dawn | Autumn, Winter, Spring |
| Wolf Pack | Cold, forested | Dusk – Dawn | Autumn, Winter, Spring |
| Hyenas | Hot, dry, open | Dusk – Dawn | All |
| Spring Peepers | Cool, wet | Dusk – Dawn | Spring |
| Summer Frogs & Crickets | Warm, wet | Dusk – Night | Summer |
| Frog Chorus | Warm, wet | Dusk – Dawn | Spring, Summer |
| Autumn Crickets | Cool, moderate rainfall | Dusk – Dawn | Autumn |
| Cicadas | Hot, forested | Morning – Afternoon | Summer |
| Squirrels | Warm, some vegetation | Morning – Afternoon | All |
| Woodpeckers | Temperate, forested | Dawn – Afternoon | Spring, Summer, Autumn |
| Flies | Near gnat swarms (proximity) | Any | All |

### Underground

| Group | Where | Time | Season |
|---|---|---|---|
| Cave Drips | Underground (low skylight) | Any | All |
| Falling Stones | Underground | Any | All |
| Bats | Underground | Any | All |
| Cave Rustling | Underground | Any | All |

---

## Sounds Included

82 audio files across 22 sound types:

- **Cardinals / Songbirds** — cardinal_call 1–4, songbird_call 1–4
- **Mourning Doves** — mourning_dove 1–2
- **Loons** — loon_call 1–7
- **Crows** — crow_call 1–3
- **Crows in Flight** — crow_flight 1–4
- **Ravens** — raven_call 1
- **Owls** — owl_hoot 1–3
- **Barn Owl Screeches** — owl_screech 1–4
- **Wolves** — wolf_howl 1–3
- **Wolf Pack** — wolf_pack 1–4
- **Hyenas** — hyena_laugh 1–2, hyena_whoop 1–2
- **Frogs** — frog_croak 1–3
- **Frog Chorus** — frog_chorus 1–4
- **Crickets** — cricket_chirp 1–3
- **Cicadas** — cicada_buzz 1–3
- **Squirrels** — squirrel_chitter 1–3
- **Woodpeckers** — woodpecker_peck 1–3
- **Flies** — fly_buzz 1–4
- **Cave Drips** — cave_drip 1–4
- **Falling Stones** — cave_stone 1–4
- **Bats** — bat_screech 1–4
- **Cave Rustling** — cave_rustle 1–4

---

## In-Game Commands

All commands are typed into the chat window.

| Command | What it does |
|---|---|
| `.son status` | Shows your current temperature, rainfall, forest density, time of day, season, and which sound groups are eligible at your location. Use this to diagnose why certain sounds aren't playing. |
| `.son on` | Enables the mod (on by default) |
| `.son off` | Disables all ambient sounds |
| `.son vol 0.5` | Sets a master volume multiplier from 0.0 to 2.0. Default is 1.0. Use this to globally scale sounds up or down. |
| `.son reload` | Hot-reloads `soundgroups.json` and your settings file without restarting the game. |
| `.son nearby [radius]` | Lists entity types within the given radius (default 30 blocks) and the distance to the nearest of each. A diagnostic tool for checking what's around you. |

### Turning down one particular sound

Every group can be tuned individually, and your changes are saved automatically.

| Command | What it does |
|---|---|
| `.son groups` | Lists every sound group — its id, its name, and your current settings for it. Start here to find the id you want. |
| `.son gvol cicadas 0.3` | Makes just that group quieter (or louder). `1.0` is default, `0.0` is silent, `2.0` is twice as loud. |
| `.son gspace cicadas 3` | Makes just that group play less often — `3` means roughly a third as often. `1.0` is default. |
| `.son mute cicadas` | Silences that group completely. `.son unmute cicadas` brings it back. `.son mute all` silences everything. |
| `.son space 2` | Spaces out **every** sound in the mod — `2` means everything plays about half as often. |
| `.son reset cicadas` | Restores the mod defaults for that group. `.son reset all` clears every override. |
| `.son config` | Shows where your settings file lives and lists everything you've changed. |

So if the cicadas are grating on you, `.son gvol cicadas 0.3` and
`.son gspace cicadas 3` will push them into the background, and
`.son mute cicadas` will remove them outright — no file editing, no effect on
anyone else on the server.

### Tip: Nothing playing?

Run `.son status` and check the output. If your rainfall is 0.00 and forest density is 0.00, you are in a desert or arid open biome — only crows by day and hyenas at night will play there, which is intentional. Move to an area with some vegetation or moisture and you will hear a richer soundscape.

---

## Your Settings File

The first time the mod runs it writes a settings file to:

```
VintagestoryData/ModConfig/soundsofnature.json
```

This file is **yours**. A mod update never overwrites it, and any sound groups
added by a future version are appended to it automatically. It looks like this:

```json
{
  "enabled": true,
  "masterVolume": 1.0,
  "globalSpacing": 1.0,
  "groups": {
    "cicadas": {
      "label": "Cicadas",
      "enabled": true,
      "volume": 0.3,
      "spacing": 3.0
    }
  }
}
```

| Setting | Meaning |
|---|---|
| `masterVolume` | Scales every sound. `1.0` default, `0.5` half as loud, `0.0` silent. |
| `globalSpacing` | Scales the gap between all sounds. `2.0` = everything plays about half as often. |
| `groups.<id>.enabled` | `false` silences that one group. |
| `groups.<id>.volume` | Volume multiplier for that group only. |
| `groups.<id>.spacing` | Gap multiplier for that group only. `3.0` = a third as often. |

Every group the mod ships is listed in the file with its `label`, so you never
need to guess an id. Run `.son reload` after editing, or skip the file entirely
and use the `.son` commands above — they write to it for you.

---

## Customising Sound Groups (advanced)

The file `assets/soundsofnature/config/soundgroups.json` inside the mod zip is the
mod's own definition of the soundscape, and it controls everything the settings
file can't:

- Which sounds play and how often
- Temperature, rainfall, and forest density thresholds
- Time of day and season restrictions
- Volume range and pitch variance

**Note that this file is replaced whenever the mod updates**, so edits to it are
lost on the next version. For simply muting a group or turning one down, use the
settings file or the `.son` commands instead. Editing it also requires unpacking
and repacking the mod zip; assets are read when the game starts, so changes to a
zip in place won't be picked up.

To add new sounds, place `.ogg` files (OGG Vorbis format, 44100 Hz, stereo) into the `sounds/ambient/` folder and reference them in the config.

---

## Adding Your Own Sounds

Good sources for free wildlife audio:
- **freesound.org** — large library, filter by CC0 licence
- **xeno-canto.org** — Creative Commons bird recordings
- **Zapsplat** — free sound effects with attribution

Convert any audio to OGG Vorbis stereo 44100 Hz before adding. A quick ffmpeg command:
```
ffmpeg -i input.wav -ar 44100 -ac 2 -c:a libvorbis -q:a 5 output.ogg
```

---

## Disclaimer

I am not a developer — I am a hobbyist who built this mod through vibe coding with the help of an AI assistant. The mod is in active development, so expect ongoing changes. Feedback and improvements are welcome.
