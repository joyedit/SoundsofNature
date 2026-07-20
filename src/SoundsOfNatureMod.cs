using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
        private float masterVolume = 1.0f;
        private bool enabled = true;
        private const float HighWindThreshold = 0.5f;
        private const float FadeInSeconds      = 0.35f; // quick, gentle ramp-up at the start of each sound
        private const float IntervalMultiplier = 1.6f;  // global spacing knob: >1 spreads sounds further apart (fewer per minute)
        private const int   OutdoorSkylightMin     = 10; // ≥ this = outdoors, full volume
        private const int   UndergroundSkylightMax = 3;  // < this = true cave/underground
        private const float EnclosedVolumeMult     = 0.4f; // multiplier when skylight is 3–9

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            LoadSoundGroups();
            RegisterCommands();
            tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 2000);
            capi.Logger.Notification("[SoundsOfNature] Loaded {0} sound groups.", soundGroups.Count);
        }

        public override void Dispose()
        {
            if (capi != null)
                capi.Event.UnregisterGameTickListener(tickListenerId);
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

        #endregion

        #region Game Tick

        private void OnGameTick(float dt)
        {
            if (!enabled) return;

            var player = capi.World.Player?.Entity;
            if (player == null) return;

            BlockPos pos = player.Pos.AsBlockPos;

            // Three skylight buckets: outdoors (full vol), enclosed (attenuated), underground (suppressed).
            // Skylight is time-of-day independent so overhangs/canopies still read as outdoors.
            int skyLight = capi.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.MaxTimeOfDayLight);
            bool underground   = skyLight < UndergroundSkylightMax;
            bool enclosed      = !underground && skyLight < OutdoorSkylightMin;
            float enclosureMult = enclosed ? EnclosedVolumeMult : 1.0f;

            // Suppress all outdoor animal sounds during rain, storms, or high winds (hurricanes).
            // Note this is a flag, not an early return: the scheduling loop below still runs so each
            // due group rolls its timer forward. Returning here would freeze every timer for the whole
            // storm, then dump all the backed-up groups in one tick the moment the weather cleared.
            bool weatherMuted = false;
            if (!underground)
            {
                var weatherSys = capi.ModLoader.GetModSystem<WeatherSystemClient>();
                if (weatherSys?.clientClimateCond?.Rainfall > 0.04f)
                {
                    weatherMuted = true;
                }
                else
                {
                    Vec3d wind = capi.World.BlockAccessor.GetWindSpeedAt(pos);
                    if (wind != null && wind.Length() > HighWindThreshold)
                        weatherMuted = true;
                }
            }

            ClimateCondition climate = capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            if (climate == null) return;

            float  temperature   = climate.Temperature;
            float  rainfall      = climate.Rainfall;
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

                // Timer has been advanced above; now honor the weather suppression without playing.
                if (weatherMuted) continue;

                if (activeCounts.GetValueOrDefault(sg.Id, 0) >= sg.MaxConcurrent) continue;

                // Proximity-driven groups follow their target (cave or surface), so skip the indoor/outdoor lock.
                bool isProximity = sg.ProximityEntities.Count > 0 || sg.ProximityParticleTypes.Count > 0;
                if (!isProximity)
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

                PlayRandomSound(sg, enclosureMult, overridePos);
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
            float interval = (sg.IntervalMin + (float)(rng.NextDouble() * (sg.IntervalMax - sg.IntervalMin))) * weightFactor * IntervalMultiplier;
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
            float volume = (sg.VolumeMin + t * (sg.VolumeMax - sg.VolumeMin)) * masterVolume * enclosureMult;

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

                // A LoadSound'd one-shot is ours to clean up. Tear it down shortly after the clip
                // finishes (with a fixed fallback when the decoded length isn't known yet).
                float lenSec = sound.SoundLengthSeconds;
                int   lifeMs = lenSec > 0f ? (int)(lenSec * 1000f) + 500 : 6000;
                capi.Event.RegisterCallback(_ =>
                {
                    activeCounts[sg.Id] = Math.Max(0, activeCounts.GetValueOrDefault(sg.Id, 0) - 1);
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
                        enabled = true;
                        return TextCommandResult.Success("[SoundsOfNature] Enabled.");
                    })
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Disable ambient sounds")
                    .HandleWith(_ =>
                    {
                        enabled = false;
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
                    .WithDescription("Reload soundgroups.json")
                    .HandleWith(_ =>
                    {
                        LoadSoundGroups();
                        return TextCommandResult.Success($"[SoundsOfNature] Reloaded {soundGroups.Count} sound groups.");
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
                .EndSubCommand();
        }

        private TextCommandResult OnCmdVolume(TextCommandCallingArgs args)
        {
            float vol = (float)args[0];
            if (vol < 0f || vol > 2f)
                return TextCommandResult.Error("[SoundsOfNature] Usage: .son vol <0.0 to 2.0>");

            masterVolume = vol;
            return TextCommandResult.Success($"[SoundsOfNature] Master volume set to {masterVolume:F1}");
        }

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

            var ws = capi.ModLoader.GetModSystem<WeatherSystemClient>();
            float precip = ws?.clientClimateCond?.Rainfall ?? 0f;
            Vec3d windVec = capi.World.BlockAccessor.GetWindSpeedAt(pos);
            float windSpeed = (float)(windVec?.Length() ?? 0.0);
            bool isRaining   = !underground && precip > 0.04f;
            bool isHighWind  = !underground && windSpeed > HighWindThreshold;
            bool muted       = isRaining || isHighWind;

            float forestDensity = GetForestDensity(pos);

            capi.ShowChatMessage($"[SoundsOfNature] {(enabled ? "ON" : "OFF")} | Vol: {masterVolume:F1}x");
            if (cl != null)
                capi.ShowChatMessage($"  Temp: {cl.Temperature:F1}°C  Rain: {cl.Rainfall:F2}  Forest: {forestDensity:F2}  Precip: {precip:F2}  Wind: {windSpeed:F2}{(muted ? " (MUTED)" : "")}");
            bool nearOcean = IsOceanNearby(pos, capi.ElapsedMilliseconds);
            capi.ShowChatMessage($"  Time: {timeSlot} ({capi.World.Calendar.HourOfDay:F1}h)  Season: {season}  Location: {locationDesc} (sky {skyLight})  Ocean: {(nearOcean ? "yes" : "no")}");

            if (cl != null)
            {
                var eligible = soundGroups
                    .Where(sg =>
                        cl.Temperature   >= sg.TempMin   && cl.Temperature   <= sg.TempMax   &&
                        cl.Rainfall      >= sg.RainMin   && cl.Rainfall      <= sg.RainMax   &&
                        forestDensity    >= sg.ForestMin && forestDensity    <= sg.ForestMax &&
                        (sg.AllowedTimes.Count   == 0 || sg.AllowedTimes.Contains(timeSlot))   &&
                        (sg.AllowedSeasons.Count == 0 || sg.AllowedSeasons.Contains(season))   &&
                        (!sg.RequiresOcean || nearOcean) &&
                        (!underground || sg.PlayUnderground) &&
                        (underground || !sg.PlayUnderground))
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

        public long NextPlayTime;
    }

    #endregion
}
