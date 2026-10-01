using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Newtonsoft.Json.Linq;

namespace SoundsOfNature
{
    public class SoundsOfNatureMod : ModSystem
    {
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        private ICoreClientAPI capi;
        private List<SoundGroup> soundGroups = new();
        private Dictionary<string, int> activeCounts = new();
        private Random rng = new();
        private long tickListenerId;
        private long submergeListenerId;
        private readonly List<LiveSound> liveSounds = new();
        private bool submerged;
        private SoundsOfNatureConfig config = new();
        private const float HighWindThreshold = 0.5f;
        private const float FadeInSeconds      = 0.35f; // quick, gentle ramp-up at the start of each sound
        private const float IntervalMultiplier = 1.6f;  // global spacing knob: >1 spreads sounds further apart (fewer per minute)
        private const int   OutdoorSkylightMin     = 10; // ≥ this = outdoors, full volume
        private const int   UndergroundSkylightMax = 3;  // < this = true cave/underground
        private const float EnclosedVolumeMult     = 0.4f; // multiplier when skylight is 3–9
        private const float UnderwaterStormMult    = 0.35f; // temporal storm sounds stay faintly audible underwater
        private const float UnderwaterFadeSeconds  = 0.3f;  // how fast sounds duck when the player's head goes under

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            LoadSoundGroups();
            LoadUserConfig();
            RegisterCommands();
            tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 2000);
            // Separate, faster check so already-playing sounds duck promptly when the player dives,
            // rather than waiting up to two seconds for the main tick.
            submergeListenerId = capi.Event.RegisterGameTickListener(OnSubmergeTick, 250);
            int muted = soundGroups.Count(sg => !sg.UserEnabled);
            capi.Logger.Notification("[SoundsOfNature] Loaded {0} sound groups ({1} muted by player config).",
                soundGroups.Count, muted);
        }

        public override void Dispose()
        {
            if (capi != null)
            {
                capi.Event.UnregisterGameTickListener(tickListenerId);
                capi.Event.UnregisterGameTickListener(submergeListenerId);
            }
            base.Dispose();
        }

        #region Config Loading

        private void LoadSoundGroups()
        {
            soundGroups.Clear();
            activeCounts.Clear();

            try
            {
                IAsset asset = capi.Assets.TryGet(new AssetLocation("soundsofnature", "config/soundgroups.json"));
                if (asset == null)
                {
                    capi.Logger.Warning("[SoundsOfNature] Could not find soundgroups.json config.");
                    return;
                }

                string json = asset.ToText();
                JObject root = JObject.Parse(json);
                JArray groups = root["soundGroups"] as JArray;
                if (groups == null) return;

                foreach (JObject g in groups)
                {
                    var sg = new SoundGroup
                    {
                        Id             = g["id"]?.ToString() ?? "unknown",
                        Label          = g["label"]?.ToString() ?? "",
                        Sounds         = g["sounds"]?.ToObject<List<string>>() ?? new List<string>(),
                        IntervalMin    = g["intervalSeconds"]?["min"]?.Value<float>() ?? 10f,
                        IntervalMax    = g["intervalSeconds"]?["max"]?.Value<float>() ?? 30f,
                        VolumeMin      = g["volume"]?["min"]?.Value<float>() ?? 0.2f,
                        VolumeMax      = g["volume"]?["max"]?.Value<float>() ?? 0.4f,
                        PitchVariance  = g["pitchVariance"]?.Value<float>() ?? 0.05f,
                        MaxConcurrent  = g["maxConcurrent"]?.Value<int>() ?? 1,
                        BurstMin       = Math.Max(1, g["burst"]?["min"]?.Value<int>() ?? 1),
                        BurstMax       = Math.Max(1, g["burst"]?["max"]?.Value<int>() ?? 1),
                        Weight         = g["weight"]?.Value<int>() ?? 5,
                        PlayUnderground = g["playUnderground"]?.Value<bool>() ?? false,
                        RequiresOcean   = g["requiresOcean"]?.Value<bool>() ?? false,
                        PlayInHighWind  = g["playInHighWind"]?.Value<bool>() ?? false,
                        DuringTemporalStorm = g["duringTemporalStorm"]?.Value<bool>() ?? false,
                        ProximityEntities      = g["proximityEntities"]?.ToObject<List<string>>()      ?? new List<string>(),
                        ProximityParticleTypes = g["proximityParticleTypes"]?.ToObject<List<string>>() ?? new List<string>(),
                        ProximityRange         = g["proximityRange"]?.Value<float>()                   ?? 12f,
                    };

                    JObject cond = g["conditions"] as JObject;
                    if (cond != null)
                    {
                        sg.TempMin        = cond["temperature"]?["min"]?.Value<float>() ?? -50f;
                        sg.TempMax        = cond["temperature"]?["max"]?.Value<float>() ?? 60f;
                        sg.RainMin        = cond["rainfall"]?["min"]?.Value<float>() ?? 0f;
                        sg.RainMax        = cond["rainfall"]?["max"]?.Value<float>() ?? 1f;
                        sg.ForestMin      = cond["forestDensity"]?["min"]?.Value<float>() ?? 0f;
                        sg.ForestMax      = cond["forestDensity"]?["max"]?.Value<float>() ?? 1f;
                        sg.AllowedTimes   = cond["timeOfDay"]?.ToObject<List<string>>()  ?? new List<string>();
                        sg.AllowedSeasons = cond["seasons"]?.ToObject<List<string>>()    ?? new List<string>();
                    }

                    // Stagger initial play times so groups don't all fire at once on load
                    sg.NextPlayTime = capi.ElapsedMilliseconds + (long)(rng.NextDouble() * sg.IntervalMax * 1000);
                    soundGroups.Add(sg);
                    activeCounts[sg.Id] = 0;
                }
            }
            catch (Exception ex)
            {
                capi.Logger.Error("[SoundsOfNature] Error loading soundgroups.json: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Reads (or creates) ModConfig/soundsofnature.json and folds it onto the loaded groups.
        /// The file always lists every group the mod ships, so a player can see the exact ids to
        /// tweak without opening the mod zip.
        /// </summary>
        private void LoadUserConfig()
        {
            try
            {
                config = capi.LoadModConfig<SoundsOfNatureConfig>(SoundsOfNatureConfig.FileName)
                         ?? new SoundsOfNatureConfig();
            }
            catch (Exception ex)
            {
                // A malformed file must not take the mod down with it — fall back to defaults and
                // leave the broken file on disk so the player can fix their own typo.
                capi.Logger.Warning("[SoundsOfNature] Could not read {0} ({1}); using defaults. The file was left untouched.",
                    SoundsOfNatureConfig.FileName, ex.Message);
                config = new SoundsOfNatureConfig();
                ApplyUserConfig();
                return;
            }

            bool changed = config.SyncWith(soundGroups.Select(sg => (sg.Id, sg.Label)));
            ApplyUserConfig();
            if (changed) SaveUserConfig();
        }

        private void SaveUserConfig()
        {
            try
            {
                capi.StoreModConfig(config, SoundsOfNatureConfig.FileName);
            }
            catch (Exception ex)
            {
                capi.Logger.Warning("[SoundsOfNature] Could not save {0}: {1}", SoundsOfNatureConfig.FileName, ex.Message);
            }
        }

        /// <summary>
        /// Resolves the player's overrides into plain fields on each group, so the 2-second tick
        /// and the sound-spawning path never touch the dictionary.
        /// </summary>
        private void ApplyUserConfig()
        {
            config.Clamp();
            foreach (var sg in soundGroups)
            {
                GroupSettings gs = config.ForGroup(sg.Id);
                sg.UserEnabled = gs?.Enabled ?? true;
                sg.UserVolume  = gs?.Volume  ?? 1.0f;
                sg.UserSpacing = gs?.Spacing ?? 1.0f;
            }
        }

        #endregion

        #region Game Tick

        private void OnGameTick(float dt)
        {
            if (!config.Enabled) return;

            var player = capi.World.Player?.Entity;
            if (player == null) return;

            BlockPos pos = player.Pos.AsBlockPos;

            // Three skylight buckets: outdoors (full vol), enclosed (attenuated), underground (suppressed).
            // Skylight is time-of-day independent so overhangs/canopies still read as outdoors.
            int skyLight = capi.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.MaxTimeOfDayLight);
            bool underground   = skyLight < UndergroundSkylightMax;
            bool enclosed      = !underground && skyLight < OutdoorSkylightMin;
            float enclosureMult = enclosed ? EnclosedVolumeMult : 1.0f;

            // Suppress outdoor animal sounds during rain/storms, and most of them in high winds
            // (hurricanes) — groups with playInHighWind (e.g. seagulls) stay audible in wind.
            // Note these are flags, not an early return: the scheduling loop below still runs so each
            // due group rolls its timer forward. Returning here would freeze every timer for the whole
            // storm, then dump all the backed-up groups in one tick the moment the weather cleared.
            bool rainMuted = false;
            bool windMuted = false;
            if (!underground)
            {
                var weatherSys = capi.ModLoader.GetModSystem<WeatherSystemClient>();
                if (weatherSys?.clientClimateCond?.Rainfall > 0.04f)
                {
                    rainMuted = true;
                }
                else
                {
                    Vec3d wind = capi.World.BlockAccessor.GetWindSpeedAt(pos);
                    if (wind != null && wind.Length() > HighWindThreshold)
                        windMuted = true;
                }
            }

            bool stormActive = IsTemporalStormActive();
            OnSubmergeTick(0); // refresh the underwater flag (and duck live sounds) before gating

            ClimateCondition climate = capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            if (climate == null) return;

            float  temperature   = climate.Temperature;
            // WorldgenRainfall, not Rainfall: with NowValues the weather system's OnGetClimate
            // handler replaces Rainfall with the current precipitation level (0 when dry), while
            // WorldgenRainfall keeps the map's annual wetness the soundgroup conditions describe.
            // Using Rainfall here made every group with a rainfall.min above the rain-mute
            // threshold (0.04) unreachable: it could only pass the gate while rain was falling,
            // and falling rain had already muted it.
            float  rainfall      = climate.WorldgenRainfall;
            float  forestDensity = GetForestDensity(pos);
            string timeSlot      = GetTimeOfDaySlot(capi.World.Calendar.HourOfDay / 24.0);
            string season        = GetSeason(pos);
            long   now           = capi.ElapsedMilliseconds;

            foreach (var sg in soundGroups)
            {
                if (now < sg.NextPlayTime) continue;

                // Group is due — roll its timer forward immediately, before the gates below.
                // A group skipped for being out of its time/season/climate window (or having no
                // proximity target) would otherwise keep its stale "due" time and fire the instant
                // its window opens. With every night group due at once, that meant all of them
                // blaring together on the first tick of dusk. Rescheduling here keeps them staggered.
                ScheduleNext(sg, now);

                // A group the player muted in their own config keeps rolling its timer forward
                // (same reason as above), so unmuting mid-session doesn't dump a backlog of
                // overdue groups into one tick.
                if (!sg.UserEnabled) continue;

                // Temporal storms invert the soundscape: normal wildlife falls silent, storm-only
                // groups take over. Storm groups ignore weather mutes and the indoor/outdoor lock —
                // the storm's presence is felt everywhere, caves included.
                if (stormActive != sg.DuringTemporalStorm) continue;

                if (!sg.DuringTemporalStorm)
                {
                    // Timer has been advanced above; now honor the weather suppression without playing.
                    if (rainMuted) continue;
                    if (windMuted && !sg.PlayInHighWind) continue;
                    // Wildlife can't be heard with your head underwater. Same flag-not-return
                    // treatment as the weather mutes, so timers keep rolling while submerged.
                    if (submerged) continue;
                }

                if (activeCounts.GetValueOrDefault(sg.Id, 0) >= sg.MaxConcurrent) continue;

                // Proximity-driven groups follow their target (cave or surface), so skip the indoor/outdoor lock.
                bool isProximity = sg.ProximityEntities.Count > 0 || sg.ProximityParticleTypes.Count > 0;
                if (!isProximity && !sg.DuringTemporalStorm)
                {
                    if (underground && !sg.PlayUnderground) continue;
                    if (!underground && sg.PlayUnderground) continue;
                }

                if (temperature   < sg.TempMin   || temperature   > sg.TempMax)   continue;
                if (rainfall      < sg.RainMin    || rainfall      > sg.RainMax)   continue;
                if (forestDensity < sg.ForestMin  || forestDensity > sg.ForestMax) continue;
                if (sg.AllowedTimes.Count   > 0 && !sg.AllowedTimes.Contains(timeSlot)) continue;
                if (sg.AllowedSeasons.Count > 0 && !sg.AllowedSeasons.Contains(season)) continue;
                if (sg.RequiresOcean && !IsOceanNearby(pos, now)) continue;

                Vec3d overridePos = null;
                if (isProximity)
                {
                    if (sg.ProximityParticleTypes.Count > 0)
                    {
                        overridePos = FindNearbyParticlePos(player.Pos.XYZ, sg);
                    }
                    if (overridePos == null && sg.ProximityEntities.Count > 0)
                    {
                        Entity target = FindNearbyEntity(player.Pos.XYZ, sg);
                        if (target != null)
                            overridePos = target.Pos.XYZ.Add(0, target.SelectionBox.Y2 * 0.5, 0);
                    }
                    if (overridePos == null) continue;
                }

                // Storm sounds are otherworldly — no enclosure attenuation, they're "in your head".
                PlayRandomSound(sg, sg.DuringTemporalStorm ? 1.0f : enclosureMult, overridePos);
            }
        }

        #region Forest Density

        // ClimateCondition.ForestDensity is only populated by the server-side GetClimateAt;
        // the client-side implementation leaves it at 0 and no game system fills it in, so a
        // client-only mod must sample the region's (synced) forest map itself. Same math as
        // ServerWorldMap.AddWorldGenForestShrub.
        private float GetForestDensity(BlockPos pos)
        {
            var ba = capi.World.BlockAccessor;
            int regionSize = ba.RegionSize;
            IMapRegion region = ba.GetMapRegion(pos.X / regionSize, pos.Z / regionSize);
            if (region?.ForestMap == null || region.ForestMap.Size == 0) return 0f;

            float x = (float)((double)pos.X / regionSize % 1.0);
            float z = (float)((double)pos.Z / regionSize % 1.0);
            return region.ForestMap.GetUnpaddedColorLerpedForNormalizedPos(x, z) / 255f;
        }

        #endregion

        #region Ocean Detection

        private long     oceanCheckedMs = long.MinValue;
        private BlockPos oceanCheckedPos;
        private bool     oceanNearby;
        private const int OceanRescanMs    = 10000; // cached verdict lifetime
        private const int OceanRescanDist  = 16;    // moving farther than this invalidates the cache
        private const int OceanScanRadius  = 48;    // horizontal sample radius around the player
        private const int OceanScanStep    = 8;
        private const int OceanMinSamples  = 3;     // salty samples required, so a stray placed block doesn't count

        private bool IsOceanNearby(BlockPos playerPos, long now)
        {
            if (oceanCheckedPos != null && now - oceanCheckedMs < OceanRescanMs)
            {
                int mx = playerPos.X - oceanCheckedPos.X;
                int mz = playerPos.Z - oceanCheckedPos.Z;
                if (mx * mx + mz * mz < OceanRescanDist * OceanRescanDist)
                    return oceanNearby;
            }

            oceanCheckedMs  = now;
            oceanCheckedPos = playerPos.Copy();
            oceanNearby     = false;

            // Sample the fluid layer at the ocean surface (top water block sits just below sea
            // level), regardless of the player's own altitude — a coastal clifftop still counts.
            var ba = capi.World.BlockAccessor;
            BlockPos sample = playerPos.Copy();
            sample.Y = capi.World.SeaLevel - 1;
            int salty = 0;

            for (int dx = -OceanScanRadius; dx <= OceanScanRadius; dx += OceanScanStep)
            {
                for (int dz = -OceanScanRadius; dz <= OceanScanRadius; dz += OceanScanStep)
                {
                    sample.X = playerPos.X + dx;
                    sample.Z = playerPos.Z + dz;
                    Block fluid = ba.GetBlock(sample, BlockLayersAccess.Fluid);
                    if (fluid?.Code?.Path.StartsWithOrdinal("saltwater") == true && ++salty >= OceanMinSamples)
                    {
                        oceanNearby = true;
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region Temporal Storm

        private SystemTemporalStability stabilityCache;
        private bool stabilityLookedUp;

        private bool IsTemporalStormActive()
        {
            // GetModSystem is a dictionary walk; look it up once. The system is absent when
            // temporal stability is disabled in the world config, so remember the null too.
            if (!stabilityLookedUp)
            {
                stabilityCache = capi.ModLoader.GetModSystem<SystemTemporalStability>();
                stabilityLookedUp = true;
            }
            return stabilityCache?.StormData?.nowStormActive == true;
        }

        #endregion

        private EntityParticleSystem epsCache;
        private const int ParticleWalkLimit = 5000; // safety cap on linked-list traversal

        private Vec3d FindNearbyParticlePos(Vec3d playerPos, SoundGroup sg)
        {
            if (epsCache == null) epsCache = capi.ModLoader.GetModSystem<EntityParticleSystem>();
            if (epsCache == null) return null;

            double rangeSq = sg.ProximityRange * sg.ProximityRange;
            int matchCount = 0;
            Vec3d picked = null;

            try
            {
                ParticleBase p = epsCache.FirstAlive;
                int walked = 0;
                while (p != null && walked++ < ParticleWalkLimit)
                {
                    if (p is EntityParticle ep && ep.Alive)
                    {
                        string typeName = ep.GetType().Name;
                        bool typeMatch = false;
                        foreach (string want in sg.ProximityParticleTypes)
                        {
                            if (typeName == want || typeName.EndsWith(want, StringComparison.Ordinal))
                            {
                                typeMatch = true;
                                break;
                            }
                        }
                        if (typeMatch && ep.Position != null && ep.Position.SquareDistanceTo(playerPos) <= rangeSq)
                        {
                            // Reservoir sampling so we pick uniformly without buffering
                            matchCount++;
                            if (rng.Next(matchCount) == 0)
                                picked = new Vec3d(ep.Position.X, ep.Position.Y, ep.Position.Z);
                        }
                    }
                    p = p.Next;
                }
            }
            catch (Exception ex)
            {
                capi.Logger.Debug("[SoundsOfNature] Particle scan aborted: {0}", ex.Message);
            }

            return picked;
        }

        private Entity FindNearbyEntity(Vec3d playerPos, SoundGroup sg)
        {
            Entity[] matches = capi.World.GetEntitiesAround(playerPos, sg.ProximityRange, sg.ProximityRange, e =>
            {
                if (e == null || !e.Alive) return false;
                string code = e.Code?.ToString();
                if (string.IsNullOrEmpty(code)) return false;
                foreach (string pattern in sg.ProximityEntities)
                {
                    if (WildcardUtil.Match(pattern, code)) return true;
                }
                return false;
            });
            if (matches.Length == 0) return null;
            return matches[rng.Next(matches.Length)];
        }

        private void ScheduleNext(SoundGroup sg, long now)
        {
            // Higher weight → shorter interval → plays more frequently.
            // Weight 5 is baseline (×1.0). Weight 10 = ×0.5, weight 2 = ×2.5.
            float weightFactor = Math.Clamp(5.0f / sg.Weight, 0.2f, 5.0f);
            // UserSpacing/GlobalSpacing are the player's own knobs: >1 stretches the gap so the
            // group is heard less often, which is the least destructive way to tame a sound that
            // someone finds too frequent without silencing it outright.
            float interval = (sg.IntervalMin + (float)(rng.NextDouble() * (sg.IntervalMax - sg.IntervalMin)))
                             * weightFactor * IntervalMultiplier * sg.UserSpacing * config.GlobalSpacing;
            sg.NextPlayTime = now + (long)(interval * 1000);
        }

        private void PlayRandomSound(SoundGroup sg, float enclosureMult, Vec3d overridePos = null)
        {
            if (sg.Sounds.Count == 0) return;

            // Only free concurrency slots may be filled, so a burst never blows past MaxConcurrent.
            int free = sg.MaxConcurrent - activeCounts.GetValueOrDefault(sg.Id, 0);
            if (free <= 0) return;

            // Fire a random number of overlapping voices this trigger. Each PlayOneSound picks its
            // own random volume and position, so the burst reads as several callers at once with
            // some nearer/louder than others rather than a single sound.
            int lo = Math.Min(sg.BurstMin, sg.BurstMax);
            int hi = Math.Max(sg.BurstMin, sg.BurstMax);
            int burst = hi > 1 ? rng.Next(lo, hi + 1) : 1;
            burst = Math.Clamp(burst, 1, free);

            for (int i = 0; i < burst; i++)
                PlayOneSound(sg, enclosureMult, overridePos);
        }

        private void PlayOneSound(SoundGroup sg, float enclosureMult, Vec3d overridePos = null)
        {
            if (sg.Sounds.Count == 0) return;

            string soundPath = sg.Sounds[rng.Next(sg.Sounds.Count)];
            AssetLocation loc = new AssetLocation("soundsofnature", soundPath);

            // Clips are mono, so OpenAL applies real 3D distance attenuation from Position/Range
            // relative to the player — no need to fake "farther = quieter" here. This volume is
            // just the source's intrinsic loudness, varied uniformly within the group's range.
            float t = (float)rng.NextDouble();
            float baseVolume = (sg.VolumeMin + t * (sg.VolumeMax - sg.VolumeMin))
                               * sg.UserVolume * config.MasterVolume * enclosureMult;
            float volume     = baseVolume * UnderwaterMult(sg.DuringTemporalStorm);

            Vec3d soundPos;
            if (overridePos != null)
            {
                soundPos = overridePos;
            }
            else
            {
                var playerPos = capi.World.Player.Entity.Pos.XYZ;
                double angle    = rng.NextDouble() * Math.PI * 2.0;
                double distance = 15 + rng.NextDouble() * 35; // 15–50 blocks away
                soundPos = new Vec3d(
                    playerPos.X + Math.Cos(angle) * distance,
                    playerPos.Y + rng.NextDouble() * 10 - 2,
                    playerPos.Z + Math.Sin(angle) * distance
                );
            }

            activeCounts[sg.Id] = activeCounts.GetValueOrDefault(sg.Id, 0) + 1;
            try
            {
                var sparams = new SoundParams
                {
                    Location        = loc,
                    Position        = new Vec3f((float)soundPos.X, (float)soundPos.Y, (float)soundPos.Z),
                    ShouldLoop      = false,
                    DisposeOnFinish = false, // LoadSound'd sounds aren't auto-managed; we dispose manually below
                    Pitch           = 1f,
                    Volume          = volume,
                    Range           = 64f,
                    SoundType       = EnumSoundType.Sound,
                };

                ILoadedSound sound = capi.World.LoadSound(sparams);
                if (sound == null)
                {
                    activeCounts[sg.Id] = Math.Max(0, activeCounts.GetValueOrDefault(sg.Id, 0) - 1);
                    return;
                }

                // Start near-silent and ramp up to the target volume so the sound eases in
                // instead of popping at full level. FadeTo uses the current Params.Volume as
                // its starting point, so knock it down before starting.
                sound.SetVolume(0.0001f);
                sound.Start();
                sound.FadeTo(volume, FadeInSeconds, null);

                var live = new LiveSound { Sound = sound, BaseVolume = baseVolume, IsStorm = sg.DuringTemporalStorm };
                liveSounds.Add(live);

                // A LoadSound'd one-shot is ours to clean up. Tear it down shortly after the clip
                // finishes (with a fixed fallback when the decoded length isn't known yet).
                float lenSec = sound.SoundLengthSeconds;
                int   lifeMs = lenSec > 0f ? (int)(lenSec * 1000f) + 500 : 6000;
                capi.Event.RegisterCallback(_ =>
                {
                    activeCounts[sg.Id] = Math.Max(0, activeCounts.GetValueOrDefault(sg.Id, 0) - 1);
                    liveSounds.Remove(live);
                    if (!sound.IsDisposed) sound.Dispose();
                }, lifeMs);
            }
            catch (Exception ex)
            {
                capi.Logger.Debug("[SoundsOfNature] Could not play sound {0}: {1}", soundPath, ex.Message);
                activeCounts[sg.Id] = Math.Max(0, activeCounts.GetValueOrDefault(sg.Id, 0) - 1);
            }
        }

        #endregion

        #region Underwater

        // Eye-level fluid check rather than Swimming/FeetInLiquid, so wading or floating at the
        // surface still sounds normal — only a submerged head mutes the world above.
        private bool IsHeadSubmerged(Entity player)
        {
            double eyeY = player.Pos.Y + player.LocalEyePos.Y;
            BlockPos eyePos = new BlockPos((int)Math.Floor(player.Pos.X), (int)Math.Floor(eyeY), (int)Math.Floor(player.Pos.Z), player.Pos.Dimension);
            Block fluid = capi.World.BlockAccessor.GetBlock(eyePos, BlockLayersAccess.Fluid);
            if (fluid == null || !fluid.IsLiquid()) return false;

            // Flowing water only fills part of its block (LiquidLevel 1–7 of 7).
            double fill = Math.Max(1, fluid.LiquidLevel) / 7.0;
            return eyeY - eyePos.Y < fill;
        }

        private float UnderwaterMult(bool isStorm) => !submerged ? 1f : isStorm ? UnderwaterStormMult : 0f;

        private void OnSubmergeTick(float dt)
        {
            var player = capi.World.Player?.Entity;
            if (player == null) return;

            bool now = IsHeadSubmerged(player);
            if (now == submerged) return;
            submerged = now;

            // Duck (or restore) everything already playing. FadeTo cancels any fade still in flight,
            // including a sound's initial fade-in. It clamps to a 0.01 floor, so finish muted
            // sounds off at true silence once the fade lands.
            foreach (var live in liveSounds)
            {
                if (live.Sound.IsDisposed) continue;
                float target = live.BaseVolume * UnderwaterMult(live.IsStorm);
                live.Sound.FadeTo(target, UnderwaterFadeSeconds, target > 0f ? null : s =>
                {
                    if (submerged && !s.IsDisposed) s.SetVolume(0f);
                });
            }
        }

        private class LiveSound
        {
            public ILoadedSound Sound;
            public float        BaseVolume; // pre-underwater volume, restored on surfacing
            public bool         IsStorm;
        }

        #endregion

        #region Time and Season

        private string GetTimeOfDaySlot(double normalizedHour)
        {
            if (normalizedHour < 0)   normalizedHour += 1.0;
            if (normalizedHour >= 1.0) normalizedHour -= 1.0;

            if (normalizedHour >= 0.20 && normalizedHour < 0.35) return "dawn";
            if (normalizedHour >= 0.35 && normalizedHour < 0.50) return "morning";
            if (normalizedHour >= 0.50 && normalizedHour < 0.65) return "midday";
            if (normalizedHour >= 0.65 && normalizedHour < 0.75) return "afternoon";
            if (normalizedHour >= 0.75 && normalizedHour < 0.85) return "dusk";
            return "night"; // 0.85–1.0 and 0.0–0.20
        }

        private string GetSeason(BlockPos pos)
        {
            // Use the calendar's hemisphere-aware season so it matches the in-game HUD.
            EnumSeason s = capi.World.Calendar.GetSeason(pos);
            return s switch
            {
                EnumSeason.Spring => "spring",
                EnumSeason.Summer => "summer",
                EnumSeason.Fall   => "autumn",
                EnumSeason.Winter => "winter",
                _                 => "spring"
            };
        }

        #endregion

        #region Commands

        private void RegisterCommands()
        {
            var parsers = capi.ChatCommands.Parsers;

            capi.ChatCommands.Create("son")
                .WithDescription("Sounds of Nature controls")
                .BeginSubCommand("on")
                    .WithDescription("Enable ambient sounds")
                    .HandleWith(_ =>
                    {
                        config.Enabled = true;
                        SaveUserConfig();
                        return TextCommandResult.Success("[SoundsOfNature] Enabled.");
                    })
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Disable ambient sounds")
                    .HandleWith(_ =>
                    {
                        config.Enabled = false;
                        SaveUserConfig();
                        return TextCommandResult.Success("[SoundsOfNature] Disabled.");
                    })
                .EndSubCommand()
                .BeginSubCommand("vol")
                    .WithAlias("volume")
                    .WithDescription("Set master volume (0.0 - 2.0)")
                    .WithArgs(parsers.Float("level"))
                    .HandleWith(OnCmdVolume)
                .EndSubCommand()
                .BeginSubCommand("reload")
                    .WithDescription("Reload soundgroups.json and your ModConfig settings")
                    .HandleWith(_ =>
                    {
                        LoadSoundGroups();
                        LoadUserConfig();
                        return TextCommandResult.Success($"[SoundsOfNature] Reloaded {soundGroups.Count} sound groups and {SoundsOfNatureConfig.FileName}.");
                    })
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("Show current ambient state")
                    .HandleWith(_ =>
                    {
                        ShowStatus();
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()
                .BeginSubCommand("nearby")
                    .WithDescription("List nearby entities")
                    .WithArgs(parsers.OptionalFloat("radius", 30f))
                    .HandleWith(args =>
                    {
                        ShowNearbyEntities((float)args[0]);
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()
                .BeginSubCommand("play")
                    .WithDescription("Force-play a sound group now, ignoring all conditions (testing). No arg = list group ids")
                    .WithArgs(parsers.OptionalWord("groupId"))
                    .HandleWith(OnCmdPlay)
                .EndSubCommand()
                .BeginSubCommand("groups")
                    .WithAlias("list")
                    .WithDescription("List every sound group id, its label, and your current settings for it")
                    .HandleWith(_ =>
                    {
                        ShowGroups();
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()
                .BeginSubCommand("mute")
                    .WithDescription("Silence one sound group, e.g. .son mute cicadas (or 'all')")
                    .WithArgs(parsers.Word("groupId"))
                    .HandleWith(args => OnCmdMute(args, false))
                .EndSubCommand()
                .BeginSubCommand("unmute")
                    .WithDescription("Un-silence one sound group, e.g. .son unmute cicadas (or 'all')")
                    .WithArgs(parsers.Word("groupId"))
                    .HandleWith(args => OnCmdMute(args, true))
                .EndSubCommand()
                .BeginSubCommand("gvol")
                    .WithAlias("groupvol")
                    .WithDescription("Volume multiplier for one group, 0.0-2.0. e.g. .son gvol cicadas 0.3")
                    .WithArgs(parsers.Word("groupId"), parsers.Float("multiplier"))
                    .HandleWith(OnCmdGroupVolume)
                .EndSubCommand()
                .BeginSubCommand("gspace")
                    .WithAlias("groupspacing")
                    .WithDescription("How much rarer one group should be, 0.1-20. 3 = a third as often. e.g. .son gspace cicadas 3")
                    .WithArgs(parsers.Word("groupId"), parsers.Float("multiplier"))
                    .HandleWith(OnCmdGroupSpacing)
                .EndSubCommand()
                .BeginSubCommand("space")
                    .WithAlias("spacing")
                    .WithDescription("How much rarer ALL sounds should be, 0.1-20. 2 = everything half as often")
                    .WithArgs(parsers.Float("multiplier"))
                    .HandleWith(OnCmdGlobalSpacing)
                .EndSubCommand()
                .BeginSubCommand("reset")
                    .WithDescription("Restore default settings for one group, or 'all' for everything")
                    .WithArgs(parsers.OptionalWord("groupId"))
                    .HandleWith(OnCmdReset)
                .EndSubCommand()
                .BeginSubCommand("config")
                    .WithDescription("Show where your settings file lives and what's currently overridden")
                    .HandleWith(_ =>
                    {
                        ShowConfig();
                        return TextCommandResult.Success();
                    })
                .EndSubCommand();
        }

        private TextCommandResult OnCmdVolume(TextCommandCallingArgs args)
        {
            float vol = (float)args[0];
            if (vol < 0f || vol > 2f)
                return TextCommandResult.Error("[SoundsOfNature] Usage: .son vol <0.0 to 2.0>");

            config.MasterVolume = vol;
            SaveUserConfig();
            return TextCommandResult.Success($"[SoundsOfNature] Master volume set to {config.MasterVolume:F1}x (saved to {SoundsOfNatureConfig.FileName})");
        }

        private TextCommandResult OnCmdPlay(TextCommandCallingArgs args)
        {
            string id = args[0] as string;
            if (string.IsNullOrEmpty(id))
            {
                string all = string.Join(", ", soundGroups.OrderBy(sg => sg.Id).Select(sg => sg.Id));
                return TextCommandResult.Success($"[SoundsOfNature] Usage: .son play <groupId>\nGroups: {all}");
            }

            var group = soundGroups.FirstOrDefault(sg => sg.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (group == null)
                return TextCommandResult.Error($"[SoundsOfNature] Unknown group '{id}'. Run .son play with no argument to list groups.");

            // Bypasses every gate (time, season, climate, weather mute, proximity, maxConcurrent)
            // so any group can be auditioned anywhere. Uses the normal random offset/volume path.
            PlayRandomSound(group, 1.0f, null);
            return TextCommandResult.Success($"[SoundsOfNature] Playing '{group.Id}' — {group.Label} ({group.Sounds.Count} clips, random pick).");
        }

        #region Per-Group Settings Commands

        /// <summary>
        /// Looks up a group by id for the tuning commands. Case-insensitive, and on a miss it
        /// returns an error that names the closest matches rather than just "unknown group", since
        /// the whole point of these commands is that players shouldn't have to hunt for ids.
        /// </summary>
        private bool TryResolveGroup(string id, out SoundGroup group, out TextCommandResult error)
        {
            group = soundGroups.FirstOrDefault(sg => sg.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                error = default;
                return true;
            }

            var near = soundGroups
                .Where(sg => sg.Id.Contains(id, StringComparison.OrdinalIgnoreCase)
                          || sg.Label.Contains(id, StringComparison.OrdinalIgnoreCase))
                .Select(sg => sg.Id)
                .Take(6)
                .ToList();

            string hint = near.Count > 0
                ? $"Did you mean: {string.Join(", ", near)}?"
                : "Run .son groups to see every id.";
            error = TextCommandResult.Error($"[SoundsOfNature] No sound group called '{id}'. {hint}");
            return false;
        }

        private TextCommandResult OnCmdMute(TextCommandCallingArgs args, bool unmute)
        {
            string id   = (string)args[0];
            string verb = unmute ? "Unmuted" : "Muted";

            if (id.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var sg in soundGroups)
                    config.GetOrAdd(sg.Id, sg.Label).Enabled = unmute;
                ApplyUserConfig();
                SaveUserConfig();
                return TextCommandResult.Success($"[SoundsOfNature] {verb} all {soundGroups.Count} sound groups.");
            }

            if (!TryResolveGroup(id, out SoundGroup group, out TextCommandResult error)) return error;

            config.GetOrAdd(group.Id, group.Label).Enabled = unmute;
            ApplyUserConfig();
            SaveUserConfig();
            return TextCommandResult.Success($"[SoundsOfNature] {verb} '{group.Id}' ({group.Label}). Saved.");
        }

        private TextCommandResult OnCmdGroupVolume(TextCommandCallingArgs args)
        {
            string id  = (string)args[0];
            float  mul = (float)args[1];

            if (mul < SoundsOfNatureConfig.MinVolume || mul > SoundsOfNatureConfig.MaxVolume)
                return TextCommandResult.Error($"[SoundsOfNature] Volume multiplier must be between {SoundsOfNatureConfig.MinVolume:0.##} and {SoundsOfNatureConfig.MaxVolume:0.##}. Usage: .son gvol <groupId> 0.3");

            if (!TryResolveGroup(id, out SoundGroup group, out TextCommandResult error)) return error;

            GroupSettings gs = config.GetOrAdd(group.Id, group.Label);
            gs.Volume = mul;
            ApplyUserConfig();
            SaveUserConfig();

            string note = mul == 0f ? " (silent — .son mute is the tidier way to do that)" : "";
            return TextCommandResult.Success($"[SoundsOfNature] '{group.Id}' ({group.Label}) volume set to {mul:0.##}x{note}. Saved.");
        }

        private TextCommandResult OnCmdGroupSpacing(TextCommandCallingArgs args)
        {
            string id  = (string)args[0];
            float  mul = (float)args[1];

            if (mul < SoundsOfNatureConfig.MinSpacing || mul > SoundsOfNatureConfig.MaxSpacing)
                return TextCommandResult.Error($"[SoundsOfNature] Spacing multiplier must be between {SoundsOfNatureConfig.MinSpacing:0.##} and {SoundsOfNatureConfig.MaxSpacing:0.##}. Usage: .son gspace <groupId> 3");

            if (!TryResolveGroup(id, out SoundGroup group, out TextCommandResult error)) return error;

            GroupSettings gs = config.GetOrAdd(group.Id, group.Label);
            gs.Spacing = mul;
            ApplyUserConfig();
            SaveUserConfig();

            // Show the resulting window so the number means something concrete. Mirrors the
            // arithmetic in ScheduleNext, weight factor included.
            float weightFactor = Math.Clamp(5.0f / group.Weight, 0.2f, 5.0f);
            float scale = weightFactor * IntervalMultiplier * mul * config.GlobalSpacing;
            float lo = group.IntervalMin * scale;
            float hi = group.IntervalMax * scale;
            return TextCommandResult.Success($"[SoundsOfNature] '{group.Id}' ({group.Label}) spacing set to {mul:0.##}x — roughly every {lo:F0}-{hi:F0}s. Saved.");
        }

        private TextCommandResult OnCmdGlobalSpacing(TextCommandCallingArgs args)
        {
            float mul = (float)args[0];
            if (mul < SoundsOfNatureConfig.MinSpacing || mul > SoundsOfNatureConfig.MaxSpacing)
                return TextCommandResult.Error($"[SoundsOfNature] Spacing multiplier must be between {SoundsOfNatureConfig.MinSpacing:0.##} and {SoundsOfNatureConfig.MaxSpacing:0.##}. Usage: .son space 2");

            config.GlobalSpacing = mul;
            ApplyUserConfig();
            SaveUserConfig();
            return TextCommandResult.Success($"[SoundsOfNature] All sounds now spaced {mul:0.##}x further apart. Saved.");
        }

        private TextCommandResult OnCmdReset(TextCommandCallingArgs args)
        {
            string id = args[0] as string;
            if (string.IsNullOrEmpty(id))
                return TextCommandResult.Error("[SoundsOfNature] Usage: .son reset <groupId>, or .son reset all to clear every override.");

            if (id.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                config.MasterVolume  = 1.0f;
                config.GlobalSpacing = 1.0f;
                foreach (var sg in soundGroups)
                    config.GetOrAdd(sg.Id, sg.Label).Reset();
                ApplyUserConfig();
                SaveUserConfig();
                return TextCommandResult.Success("[SoundsOfNature] All settings restored to the mod defaults.");
            }

            if (!TryResolveGroup(id, out SoundGroup group, out TextCommandResult error)) return error;

            config.GetOrAdd(group.Id, group.Label).Reset();
            ApplyUserConfig();
            SaveUserConfig();
            return TextCommandResult.Success($"[SoundsOfNature] '{group.Id}' ({group.Label}) restored to defaults. Saved.");
        }

        private void ShowGroups()
        {
            capi.ShowChatMessage($"[SoundsOfNature] {soundGroups.Count} sound groups — id, label, your settings:");
            foreach (var sg in soundGroups.OrderBy(g => g.Id))
            {
                GroupSettings gs = config.ForGroup(sg.Id);
                string state = gs?.Describe() ?? "default";
                string mark  = gs != null && !gs.IsDefault ? "*" : " ";
                capi.ShowChatMessage($" {mark} {sg.Id} — {sg.Label} [{state}]");
            }
            capi.ShowChatMessage("  Tame one: .son gvol <id> 0.3 (quieter) · .son gspace <id> 3 (rarer) · .son mute <id> (off)");
        }

        private void ShowConfig()
        {
            string path = System.IO.Path.Combine(GamePaths.ModConfig, SoundsOfNatureConfig.FileName);
            capi.ShowChatMessage($"[SoundsOfNature] Settings file: {path}");
            capi.ShowChatMessage($"  enabled: {config.Enabled} · masterVolume: {config.MasterVolume:0.##}x · globalSpacing: {config.GlobalSpacing:0.##}x");

            var tweaked = soundGroups
                .Select(sg => (sg, gs: config.ForGroup(sg.Id)))
                .Where(t => t.gs != null && !t.gs.IsDefault)
                .ToList();

            if (tweaked.Count == 0)
            {
                capi.ShowChatMessage("  No per-group overrides. Run .son groups to see the ids you can tune.");
                return;
            }

            capi.ShowChatMessage($"  {tweaked.Count} per-group override(s):");
            foreach (var (sg, gs) in tweaked)
                capi.ShowChatMessage($"    {sg.Id} — {gs.Describe()}");
        }

        #endregion

        private void ShowStatus()
        {
            var player = capi.World.Player?.Entity;
            if (player == null) return;

            BlockPos pos        = player.Pos.AsBlockPos;
            ClimateCondition cl = capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            string timeSlot     = GetTimeOfDaySlot(capi.World.Calendar.HourOfDay / 24.0);
            string season       = GetSeason(pos);
            int    skyLight     = capi.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.MaxTimeOfDayLight);
            bool   underground  = skyLight < UndergroundSkylightMax;
            bool   enclosed     = !underground && skyLight < OutdoorSkylightMin;
            string locationDesc = underground ? "underground" : enclosed ? $"enclosed (×{EnclosedVolumeMult:F1})" : "outdoors";
            bool   underwater   = IsHeadSubmerged(player);
            if (underwater) locationDesc += ", UNDERWATER (wildlife muted)";

            var ws = capi.ModLoader.GetModSystem<WeatherSystemClient>();
            float precip = ws?.clientClimateCond?.Rainfall ?? 0f;
            Vec3d windVec = capi.World.BlockAccessor.GetWindSpeedAt(pos);
            float windSpeed = (float)(windVec?.Length() ?? 0.0);
            bool isRaining   = !underground && precip > 0.04f;
            bool isHighWind  = !underground && !isRaining && windSpeed > HighWindThreshold;
            string mutedDesc = isRaining ? " (MUTED: rain)" : isHighWind ? " (MUTED: wind, except wind-tolerant groups)" : "";

            float forestDensity = GetForestDensity(pos);

            int mutedCount = soundGroups.Count(sg => !sg.UserEnabled);
            string spacingDesc = config.GlobalSpacing == 1.0f ? "" : $" | Spacing: {config.GlobalSpacing:0.##}x";
            string mutedDescCfg = mutedCount == 0 ? "" : $" | {mutedCount} group(s) muted — see .son groups";
            capi.ShowChatMessage($"[SoundsOfNature] {(config.Enabled ? "ON" : "OFF")} | Vol: {config.MasterVolume:F1}x{spacingDesc}{mutedDescCfg}");
            if (cl != null)
                capi.ShowChatMessage($"  Temp: {cl.Temperature:F1}°C  Rain: {cl.WorldgenRainfall:F2}  Forest: {forestDensity:F2}  Precip: {precip:F2}  Wind: {windSpeed:F2}{mutedDesc}");
            bool nearOcean = IsOceanNearby(pos, capi.ElapsedMilliseconds);
            bool stormActive = IsTemporalStormActive();
            capi.ShowChatMessage($"  Time: {timeSlot} ({capi.World.Calendar.HourOfDay:F1}h)  Season: {season}  Location: {locationDesc} (sky {skyLight})  Ocean: {(nearOcean ? "yes" : "no")}  Temporal storm: {(stormActive ? "ACTIVE" : "no")}");

            if (cl != null)
            {
                var eligible = soundGroups
                    .Where(sg =>
                        sg.UserEnabled &&
                        sg.DuringTemporalStorm == stormActive &&
                        cl.Temperature   >= sg.TempMin   && cl.Temperature   <= sg.TempMax   &&
                        cl.WorldgenRainfall >= sg.RainMin && cl.WorldgenRainfall <= sg.RainMax &&
                        forestDensity    >= sg.ForestMin && forestDensity    <= sg.ForestMax &&
                        (sg.AllowedTimes.Count   == 0 || sg.AllowedTimes.Contains(timeSlot))   &&
                        (sg.AllowedSeasons.Count == 0 || sg.AllowedSeasons.Contains(season))   &&
                        (!sg.RequiresOcean || nearOcean) &&
                        (!underwater || sg.DuringTemporalStorm) &&
                        (sg.DuringTemporalStorm ||
                            ((!underground || sg.PlayUnderground) &&
                             (underground || !sg.PlayUnderground))))
                    .Select(sg => sg.Label);

                capi.ShowChatMessage($"  Eligible: {string.Join(", ", eligible)}");
            }
        }

        private void ShowNearbyEntities(float radius)
        {
            var player = capi.World.Player?.Entity;
            if (player == null) return;

            Vec3d ppos = player.Pos.XYZ;
            Entity[] entities = capi.World.GetEntitiesAround(ppos, radius, radius, e => e != null && e != player);

            if (entities.Length == 0)
            {
                capi.ShowChatMessage($"[SoundsOfNature] No entities within {radius:F0} blocks.");
                return;
            }

            var counts = new Dictionary<string, (int count, double nearest)>();
            foreach (var e in entities)
            {
                string code = e.Code?.ToString() ?? "unknown";
                double dist = e.Pos.XYZ.DistanceTo(ppos);
                if (counts.TryGetValue(code, out var cur))
                    counts[code] = (cur.count + 1, Math.Min(cur.nearest, dist));
                else
                    counts[code] = (1, dist);
            }

            capi.ShowChatMessage($"[SoundsOfNature] Entities within {radius:F0} blocks:");
            foreach (var kv in counts.OrderBy(k => k.Value.nearest))
            {
                capi.ShowChatMessage($"  {kv.Key} ×{kv.Value.count} (nearest {kv.Value.nearest:F1}m)");
            }
        }

        #endregion
    }

    #region Data Classes

    internal class SoundGroup
    {
        public string Id;
        public string Label;
        public List<string> Sounds = new();

        public float TempMin, TempMax;
        public float RainMin, RainMax;
        public float ForestMin, ForestMax;
        public List<string> AllowedTimes   = new();
        public List<string> AllowedSeasons = new();
        public bool  PlayUnderground = false;
        public bool  RequiresOcean   = false; // only fires when saltwater is found near the player at sea level
        public bool  PlayInHighWind  = false; // exempt from the high-wind mute (still muted by rain)
        public bool  DuringTemporalStorm = false; // only during temporal storms; normal groups are silenced while one rages

        public float IntervalMin, IntervalMax;
        public float VolumeMin, VolumeMax;
        public float PitchVariance;
        public int   MaxConcurrent;
        public int   BurstMin = 1, BurstMax = 1; // voices fired per trigger; >1 = overlapping chorus
        public int   Weight;

        // When non-empty, this group only fires if a matching entity is within ProximityRange of the player.
        // Sound plays from the entity's position rather than a random offset.
        public List<string> ProximityEntities = new();
        public List<string> ProximityParticleTypes = new(); // simple class-name match, e.g. "EntityParticleMatingGnats"
        public float        ProximityRange    = 12f;

        // Player overrides from ModConfig/soundsofnature.json, resolved once on load rather than
        // looked up per tick. Multipliers on the values above; UserEnabled false = fully muted.
        public bool  UserEnabled = true;
        public float UserVolume  = 1.0f;
        public float UserSpacing = 1.0f;

        public long NextPlayTime;
    }

    #endregion
}
