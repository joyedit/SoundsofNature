using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SoundsOfNature
{
    /// <summary>
    /// Player-owned settings, stored in VintagestoryData/ModConfig/soundsofnature.json.
    ///
    /// Deliberately separate from assets/soundsofnature/config/soundgroups.json: that file is the
    /// author's definition of the soundscape, it lives inside the mod zip, and it is replaced
    /// wholesale every time the mod updates — so anything a player edits there is lost (and, since
    /// assets are read once at startup, usually never took effect in the first place). Everything
    /// in this file survives updates and is a pure multiplier on top of the shipped defaults.
    /// </summary>
    public class SoundsOfNatureConfig
    {
        public const string FileName = "soundsofnature.json";

        // Rewritten from Defaults on every load so the notes stay current as the mod gains options.
        [JsonProperty("readme", Order = -1)]
        public string[] Readme = DefaultReadme;

        [JsonProperty("enabled", Order = 0)]
        public bool Enabled = true;

        [JsonProperty("masterVolume", Order = 1)]
        public float MasterVolume = 1.0f;

        [JsonProperty("globalSpacing", Order = 2)]
        public float GlobalSpacing = 1.0f;

        [JsonProperty("groups", Order = 3)]
        public Dictionary<string, GroupSettings> Groups = NewGroupMap();

        public static readonly string[] DefaultReadme =
        {
            "Sounds of Nature - player settings. Safe to edit; survives mod updates.",
            "Use '.son reload' in game to apply changes without restarting, or edit it live with",
            "the .son commands (.son groups, .son mute <id>, .son gvol <id> <n>, .son gspace <id> <n>).",
            "",
            "masterVolume   : scales every sound. 1.0 = default, 0.5 = half as loud, 0 = silent.",
            "globalSpacing  : scales the gap between ALL sounds. 2.0 = everything plays half as often.",
            "groups.<id>.enabled : false silences that group completely.",
            "groups.<id>.volume  : volume multiplier for that group only. 0.3 = much quieter.",
            "groups.<id>.spacing : gap multiplier for that group only. 3.0 = plays a third as often.",
            "",
            "'label' is informational only - the key above it is the id the .son commands take.",
        };

        public const float MinVolume  = 0.0f;
        public const float MaxVolume  = 2.0f;
        public const float MinSpacing = 0.1f;
        public const float MaxSpacing = 20.0f;

        // Group ids are matched case-insensitively so a player typing "Cicadas" in the file or a
        // command still lands on the "cicadas" group.
        public static Dictionary<string, GroupSettings> NewGroupMap() =>
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Brings the file in line with the mod's current group list: fills in any group the
        /// player's file has never seen (new mod version, or first run) and refreshes labels and
        /// the readme. Entries for unknown ids are left untouched so downgrading doesn't discard
        /// a player's tweaks. Returns true when the file needs writing back.
        /// </summary>
        public bool SyncWith(IEnumerable<(string Id, string Label)> knownGroups)
        {
            bool changed = false;

            if (Readme == null || !ReadmeMatchesDefault())
            {
                Readme  = DefaultReadme;
                changed = true;
            }

            // A hand-edited file deserializes into whatever dictionary Newtonsoft built, which is
            // case-sensitive; rebuild it so lookups stay forgiving.
            if (Groups == null)
            {
                Groups  = NewGroupMap();
                changed = true;
            }
            else if (!ReferenceEquals(Groups.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                var rebuilt = NewGroupMap();
                foreach (var kv in Groups) rebuilt[kv.Key] = kv.Value ?? new GroupSettings();
                Groups = rebuilt;
            }

            foreach (var (id, label) in knownGroups)
            {
                if (!Groups.TryGetValue(id, out GroupSettings gs) || gs == null)
                {
                    Groups[id] = new GroupSettings { Label = label };
                    changed    = true;
                    continue;
                }

                if (gs.Label != label)
                {
                    gs.Label = label;
                    changed  = true;
                }
            }

            Clamp();
            return changed;
        }

        private bool ReadmeMatchesDefault()
        {
            if (Readme.Length != DefaultReadme.Length) return false;
            for (int i = 0; i < Readme.Length; i++)
                if (Readme[i] != DefaultReadme[i]) return false;
            return true;
        }

        /// <summary>Keeps hand-edited values inside sane bounds — a volume of 50 would deafen.</summary>
        public void Clamp()
        {
            MasterVolume  = Math.Clamp(MasterVolume,  MinVolume,  MaxVolume);
            GlobalSpacing = Math.Clamp(GlobalSpacing, MinSpacing, MaxSpacing);

            if (Groups == null) return;
            foreach (var gs in Groups.Values)
            {
                if (gs == null) continue;
                gs.Volume  = Math.Clamp(gs.Volume,  MinVolume,  MaxVolume);
                gs.Spacing = Math.Clamp(gs.Spacing, MinSpacing, MaxSpacing);
            }
        }

        public GroupSettings ForGroup(string id)
        {
            if (Groups != null && Groups.TryGetValue(id, out GroupSettings gs) && gs != null) return gs;
            return null;
        }

        /// <summary>Gets the group's settings, creating them if the file predates that group.</summary>
        public GroupSettings GetOrAdd(string id, string label)
        {
            GroupSettings gs = ForGroup(id);
            if (gs != null) return gs;

            Groups ??= NewGroupMap();
            gs = new GroupSettings { Label = label };
            Groups[id] = gs;
            return gs;
        }
    }

    public class GroupSettings
    {
        [JsonProperty("label", Order = 0)]
        public string Label;

        [JsonProperty("enabled", Order = 1)]
        public bool Enabled = true;

        [JsonProperty("volume", Order = 2)]
        public float Volume = 1.0f;

        [JsonProperty("spacing", Order = 3)]
        public float Spacing = 1.0f;

        [JsonIgnore]
        public bool IsDefault => Enabled && Volume == 1.0f && Spacing == 1.0f;

        public void Reset()
        {
            Enabled = true;
            Volume  = 1.0f;
            Spacing = 1.0f;
        }

        /// <summary>Short "muted", "vol 0.30, spacing 3.0" style summary for chat output.</summary>
        public string Describe()
        {
            if (!Enabled) return "muted";
            if (IsDefault) return "default";

            var parts = new List<string>(2);
            if (Volume  != 1.0f) parts.Add($"vol {Volume:0.##}x");
            if (Spacing != 1.0f) parts.Add($"spacing {Spacing:0.##}x");
            return string.Join(", ", parts);
        }
    }
}
