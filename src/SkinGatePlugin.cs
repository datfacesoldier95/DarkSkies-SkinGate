using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using UnityEngine;

namespace SkinGate
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SkinGatePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.darkskies.skingate";
        public const string PluginName = "SkinGate";
        public const string PluginVersion = "1.1.3";

        public static SkinGatePlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<string> AllowlistPath;
        public static ConfigEntry<KeyCode> ReloadKey;
        public static ConfigEntry<bool> EnforceOnHost;
        public static ConfigEntry<bool> FilterUi;

        private Harmony _harmony;
        private float _nextAllowlistPoll;
        private DateTime _seenWriteTimeUtc;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            var defaultAllowlist = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", "allowlist.json");
            AllowlistPath = Config.Bind(
                "General",
                "AllowlistPath",
                defaultAllowlist,
                "Path to allowlist.json (default: next to SkinGate.dll). /sync-skins can also copy here for squad PCs.");

            ReloadKey = Config.Bind(
                "General",
                "ReloadKey",
                KeyCode.F11,
                "Hotkey to reload allowlist.json mid-session after /sync-skins.");

            EnforceOnHost = Config.Bind(
                "General",
                "EnforceSetLiveryKey",
                true,
                "Block Aircraft.SetLiveryKey when the owner's SteamID is not allowed that skin.");

            FilterUi = Config.Bind(
                "General",
                "FilterLiveryUi",
                true,
                "Filter LoadoutSelector.GetLiveryOptions for the local player's SteamID.");

            if (AllowlistSession.IsHostAllowlistSource)
            {
                var allowlistPath = ResolveAllowlistPath(AllowlistPath.Value);
                AllowlistStore.Reload(allowlistPath);
                _seenWriteTimeUtc = AllowlistStore.FileWriteTimeUtc;
            }
            else
            {
                AllowlistStore.SetExpectHostAllowlist(true);
            }

            gameObject.AddComponent<AllowlistNetworkSync>();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            if (AllowlistSession.IsHostAllowlistSource)
            {
                var allowlistPath = ResolveAllowlistPath(AllowlistPath.Value);
                Log.LogInfo($"{PluginName} {PluginVersion} loaded. Host allowlist: {allowlistPath}");
                if (AllowlistStore.Data.Players.Count == 0)
                {
                    Log.LogWarning(
                        "Allowlist has 0 players — run Discord `/sync-skins` and press F11 on the host PC.");
                }
            }
            else
            {
                Log.LogInfo($"{PluginName} {PluginVersion} loaded. Client mode — allowlist will sync from host.");
            }
        }

        private void Update()
        {
            if (AllowlistSession.IsHostAllowlistSource)
            {
                if (Input.GetKeyDown(ReloadKey.Value))
                {
                    var path = ResolveAllowlistPath(AllowlistPath.Value);
                    AllowlistStore.Reload(path);
                    _seenWriteTimeUtc = AllowlistStore.FileWriteTimeUtc;
                    AllowlistNetworkSync.HostBroadcastAllowlist();
                    Log.LogInfo($"Allowlist reloaded via {ReloadKey.Value} ({path}).");
                }

                // Auto-reload when /sync-skins rewrites the file (host PC only).
                if (Time.unscaledTime >= _nextAllowlistPoll)
                {
                    _nextAllowlistPoll = Time.unscaledTime + 2f;
                    try
                    {
                        var path = ResolveAllowlistPath(AllowlistPath.Value);
                        if (File.Exists(path))
                        {
                            var writeTime = File.GetLastWriteTimeUtc(path);
                            if (writeTime != _seenWriteTimeUtc)
                            {
                                AllowlistStore.Reload(path);
                                _seenWriteTimeUtc = AllowlistStore.FileWriteTimeUtc;
                                AllowlistNetworkSync.HostBroadcastAllowlist();
                                Log.LogInfo("Allowlist reloaded (file changed) and broadcast to clients.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning($"Allowlist poll failed: {ex.Message}");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>Config path, or legacy host path, or plugin folder copy.</summary>
        internal static string ResolveAllowlistPath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            var legacy = @"C:\DarkSkies\Discord Bot\data\allowlist.json";
            if (File.Exists(legacy))
                return legacy;

            var besideDll = Path.Combine(Path.GetDirectoryName(Instance?.Info?.Location ?? "") ?? "", "allowlist.json");
            if (File.Exists(besideDll))
                return besideDll;

            return string.IsNullOrWhiteSpace(configuredPath) ? besideDll : configuredPath;
        }
    }

    public static class LiveryEnforcer
    {
        /// <summary>
        /// Re-entrancy guard when probing GetLiveryOptions from SetLiveryKey.
        /// </summary>
        [ThreadStatic]
        private static bool _probingOptions;

        /// <summary>
        /// If the key is not allowed for this SteamID, replace with a safe fallback (never leaves UI with a blocked call).
        /// When this airframe has no squadron skin in its options list, vanilla builtins are allowed.
        /// </summary>
        public static bool SanitizeForPlayer(ulong steamId, ref LiveryKey liveryKey, bool hangarPreview, Aircraft aircraft = null)
        {
            if (steamId == 0)
                return true;

            if (AllowlistStore.IsAllowed(steamId, liveryKey))
                return true;

            // Airframe with no squadron skin → vanilla builtins are fine.
            if (liveryKey.Type == LiveryKey.KeyType.Builtin
                && !AirframeHasSquadronSkin(steamId, aircraft))
            {
                return true;
            }

            // Prefer a squadron skin that actually exists on this airframe.
            if (TryFirstAllowedOnAircraft(steamId, aircraft, out var onAirframe))
            {
                SkinGatePlugin.Log?.LogInfo(
                    $"Replaced disallowed livery with airframe-safe {onAirframe} for SteamID {steamId}.");
                liveryKey = onAirframe;
                return true;
            }

            // Hangar preview on an airframe without squadron skins: keep builtin rather than forcing a wrong Workshop key.
            if (hangarPreview && liveryKey.Type == LiveryKey.KeyType.Builtin)
                return true;

            var replacement = AllowlistStore.GetFallbackKey(steamId);
            if (replacement.HasValue)
            {
                SkinGatePlugin.Log?.LogInfo(
                    $"Replaced disallowed livery with {replacement.Value} for SteamID {steamId}.");
                liveryKey = replacement.Value;
                return true;
            }

            SkinGatePlugin.Log?.LogWarning(
                $"Blocked livery {liveryKey} for SteamID {steamId} (no fallback).");
            return false;
        }

        public static bool ShouldEnforceNetworkSync()
        {
            // Host rewrites illegal sync from clients; each client also enforces locally before sending.
            return SkinGatePlugin.EnforceOnHost.Value;
        }

        /// <summary>
        /// True when this airframe's livery list includes at least one explicitly allowlisted custom skin.
        /// </summary>
        public static bool AirframeHasSquadronSkin(ulong steamId, Aircraft aircraft)
        {
            if (aircraft == null)
                return AllowlistStore.Data.PlayerHasExplicitCustom(steamId);

            if (TryGetRawOptions(aircraft, out var options))
                return AllowlistStore.Data.OptionsContainExplicitAllow(steamId, options);

            // Cache from last hangar filter for this definition (local UI path).
            var defName = aircraft.definition != null ? aircraft.definition.name : null;
            if (!string.IsNullOrEmpty(defName)
                && AllowlistStore.TryGetCachedAirframeMode(steamId, defName, out var hasSquadron))
            {
                return hasSquadron;
            }

            return AllowlistStore.Data.PlayerHasExplicitCustom(steamId);
        }

        public static bool TryFirstAllowedOnAircraft(ulong steamId, Aircraft aircraft, out LiveryKey key)
        {
            key = default;
            if (!TryGetRawOptions(aircraft, out var options))
                return false;

            foreach (var (opt, _) in options)
            {
                if (AllowlistStore.Data.IsExplicitlyAllowed(steamId, opt))
                {
                    key = opt;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetRawOptions(Aircraft aircraft, out List<ValueTuple<LiveryKey, string>> options)
        {
            options = null;
            if (aircraft == null || aircraft.definition == null || _probingOptions)
                return false;

            try
            {
                options = new List<ValueTuple<LiveryKey, string>>();
                _probingOptions = true;
                // Postfix skips while probing so we see the full airframe list.
                LoadoutSelector.GetLiveryOptions(options, aircraft.definition, null, true);
                return options.Count > 0;
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"Airframe livery probe failed: {ex.Message}");
                options = null;
                return false;
            }
            finally
            {
                _probingOptions = false;
            }
        }

        internal static bool IsProbingOptions => _probingOptions;
    }

    public static class AllowlistStore
    {
        public static AllowlistData Data { get; private set; } = AllowlistData.Empty();
        public static DateTime LoadedAt { get; private set; }
        public static DateTime FileWriteTimeUtc { get; private set; }
        public static string LoadedPath { get; private set; }
        public static bool HasHostAllowlist { get; private set; }
        public static bool ExpectHostAllowlist { get; private set; }

        public static void SetExpectHostAllowlist(bool expect)
        {
            ExpectHostAllowlist = expect;
        }

        public static void ApplyHostJson(string json)
        {
            try
            {
                Data = AllowlistData.FromJson(json);
                LoadedAt = DateTime.UtcNow;
                HasHostAllowlist = true;
                LoadedPath = "(from host)";
                AirframeSquadronCache.Clear();
                var hash = AllowlistNetworkSync.ShortHash(json);
                SkinGatePlugin.Log?.LogInfo(
                    $"Allowlist synced from host ({Data.Players.Count} players) hash={hash}.");
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogError($"Host allowlist apply failed: {ex}");
            }
        }

        public static void InvalidateHostAllowlist()
        {
            HasHostAllowlist = false;
            AirframeSquadronCache.Clear();
        }

        public static void Reload(string path)
        {
            LoadedPath = path;
            try
            {
                if (!File.Exists(path))
                {
                    SkinGatePlugin.Log?.LogWarning($"Allowlist not found: {path}. Denying all non-explicit skins.");
                    Data = AllowlistData.Empty();
                    LoadedAt = DateTime.UtcNow;
                    FileWriteTimeUtc = DateTime.MinValue;
                    HasHostAllowlist = false;
                    return;
                }

                FileWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                var json = File.ReadAllText(path);
                Data = AllowlistData.FromJson(json);
                LoadedAt = DateTime.UtcNow;
                HasHostAllowlist = true;
                AirframeSquadronCache.Clear();
                SkinGatePlugin.Log?.LogInfo(
                    $"Allowlist loaded ({Data.Players.Count} players) from {path}");
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogError($"Failed to load allowlist: {ex}");
                Data = AllowlistData.Empty();
                LoadedAt = DateTime.UtcNow;
            }
        }

        // steamId|definitionName → airframe offered a squadron skin in last hangar filter
        private static readonly Dictionary<string, bool> AirframeSquadronCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public static bool IsAllowed(ulong steamId, LiveryKey key)
        {
            return Data.IsAllowed(steamId, key);
        }

        public static void CacheAirframeMode(ulong steamId, string definitionName, bool hasSquadronSkin)
        {
            if (string.IsNullOrEmpty(definitionName))
                return;
            AirframeSquadronCache[$"{steamId}|{definitionName}"] = hasSquadronSkin;
        }

        public static bool TryGetCachedAirframeMode(ulong steamId, string definitionName, out bool hasSquadronSkin)
        {
            return AirframeSquadronCache.TryGetValue($"{steamId}|{definitionName}", out hasSquadronSkin);
        }

        public static LiveryKey? FirstAllowedOrNull(ulong steamId, IEnumerable<(LiveryKey key, string label)> options)
        {
            foreach (var (key, _) in options)
            {
                if (IsAllowed(steamId, key))
                    return key;
            }
            return null;
        }

        public static LiveryKey? GetFallbackKey(ulong steamId)
        {
            var steamKey = steamId.ToString();
            if (Data.Players.TryGetValue(steamKey, out var player))
            {
                foreach (var entry in player.Allowed)
                {
                    if (entry.Type != null && entry.Type.Equals("Workshop", StringComparison.OrdinalIgnoreCase)
                        && ulong.TryParse(entry.Id, out _))
                    {
                        return new LiveryKey(LiveryKey.KeyType.Workshop, 0, entry.Id);
                    }
                }
            }

            if (Data.IsAllowed(steamId, BuiltinKey(0)))
                return BuiltinKey(0);

            for (var i = 0; i < 16; i++)
            {
                var k = BuiltinKey(i);
                if (Data.IsAllowed(steamId, k))
                    return k;
            }

            return null;
        }

        private static LiveryKey BuiltinKey(int index) => new LiveryKey(index);
    }

    public sealed class AllowlistData
    {
        public string DefaultPolicy { get; set; } = "deny";
        public bool ExcludeBuiltinByDefault { get; set; } = true;
        public bool NoSquadronAllowBuiltin { get; set; } = true;
        public Dictionary<string, PlayerAllow> Players { get; set; } = new Dictionary<string, PlayerAllow>(StringComparer.Ordinal);

        public static AllowlistData Empty() => new AllowlistData();

        public static AllowlistData FromJson(string json)
        {
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var data = new AllowlistData
            {
                DefaultPolicy = root.Value<string>("defaultPolicy") ?? "deny",
                ExcludeBuiltinByDefault = root.Value<bool?>("excludeBuiltinByDefault") ?? true,
                NoSquadronAllowBuiltin = root.Value<bool?>("noSquadronAllowBuiltin") ?? true,
            };

            var players = root["players"] as Newtonsoft.Json.Linq.JObject;
            if (players != null)
            {
                foreach (var prop in players.Properties())
                {
                    var p = prop.Value;
                    var allow = new PlayerAllow
                    {
                        AllowBuiltin = p.Value<bool?>("allowBuiltin") ?? false,
                    };
                    var allowed = p["allowed"] as Newtonsoft.Json.Linq.JArray;
                    if (allowed != null)
                    {
                        foreach (var entry in allowed)
                        {
                            allow.Allowed.Add(SkinEntry.FromToken(entry));
                        }
                    }
                    data.Players[prop.Name] = allow;
                }
            }

            return data;
        }

        public bool IsAllowed(ulong steamId, LiveryKey key)
        {
            var steamKey = steamId.ToString();
            if (!Players.TryGetValue(steamKey, out var player))
            {
                // Unlinked / unknown: vanilla builtins only when noSquadronAllowBuiltin is on.
                return NoSquadronAllowBuiltin && key.Type == LiveryKey.KeyType.Builtin;
            }

            if (key.Type == LiveryKey.KeyType.Builtin)
            {
                if (player.AllowBuiltin)
                    return true;
                if (Matches(player, key))
                    return true;
                // When excludeBuiltinByDefault is false, builtins are allowed without an explicit entry.
                return !ExcludeBuiltinByDefault;
            }

            return Matches(player, key);
        }

        /// <summary>True if key matches an explicit allowlist entry (not allowBuiltin).</summary>
        public bool IsExplicitlyAllowed(ulong steamId, LiveryKey key)
        {
            var steamKey = steamId.ToString();
            if (!Players.TryGetValue(steamKey, out var player))
                return false;
            return Matches(player, key);
        }

        public bool PlayerHasExplicitCustom(ulong steamId)
        {
            var steamKey = steamId.ToString();
            if (!Players.TryGetValue(steamKey, out var player))
                return false;

            foreach (var entry in player.Allowed)
            {
                if (entry.Type == null)
                    continue;
                if (entry.Type.Equals("Workshop", StringComparison.OrdinalIgnoreCase)
                    || entry.Type.Equals("AppData", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public bool OptionsContainExplicitAllow(ulong steamId, IEnumerable<ValueTuple<LiveryKey, string>> options)
        {
            foreach (var (key, _) in options)
            {
                if (IsExplicitlyAllowed(steamId, key))
                    return true;
            }

            return false;
        }

        public string GetCustomLabel(ulong steamId, LiveryKey key)
        {
            var steamKey = steamId.ToString();
            if (!Players.TryGetValue(steamKey, out var player))
                return null;

            foreach (var entry in player.Allowed)
            {
                if (entry.Matches(key) && !string.IsNullOrWhiteSpace(entry.Label))
                    return entry.Label;
            }
            return null;
        }

        private static bool Matches(PlayerAllow player, LiveryKey key)
        {
            foreach (var entry in player.Allowed)
            {
                if (entry.Matches(key))
                    return true;
            }
            return false;
        }
    }

    public sealed class PlayerAllow
    {
        public bool AllowBuiltin { get; set; }
        public List<SkinEntry> Allowed { get; set; } = new List<SkinEntry>();
    }

    public sealed class SkinEntry
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Index { get; set; }
        /// <summary>Custom name shown in the livery dropdown (overrides author title).</summary>
        public string Label { get; set; }

        public static SkinEntry FromToken(Newtonsoft.Json.Linq.JToken token)
        {
            return new SkinEntry
            {
                Type = token.Value<string>("type"),
                Id = token.Value<string>("id"),
                Name = token.Value<string>("name"),
                Index = token.Value<int?>("index"),
                Label = token.Value<string>("label") ?? token.Value<string>("displayName"),
            };
        }

        public bool Matches(LiveryKey key)
        {
            if (string.IsNullOrEmpty(Type))
                return false;

            if (Type.Equals("Workshop", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Type != LiveryKey.KeyType.Workshop)
                    return false;
                if (string.IsNullOrEmpty(Id))
                    return false;
                return ulong.TryParse(Id, out var id) && key.Id == id;
            }

            if (Type.Equals("AppData", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Type != LiveryKey.KeyType.AppData)
                    return false;
                return string.Equals(Name, key.AppDataName, StringComparison.OrdinalIgnoreCase);
            }

            if (Type.Equals("Builtin", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Type != LiveryKey.KeyType.Builtin)
                    return false;
                if (!Index.HasValue)
                    return true; // allow all builtins if type-only entry
                return key.Index == Index.Value;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.GetLiveryOptions))]
    public static class Patch_GetLiveryOptions
    {
        [HarmonyPostfix]
        public static void Postfix(List<ValueTuple<LiveryKey, string>> resultsList, AircraftDefinition aircraft)
        {
            try
            {
                // Probe from SetLiveryKey needs the unfiltered airframe list.
                if (LiveryEnforcer.IsProbingOptions)
                    return;

                if (!SkinGatePlugin.FilterUi.Value || resultsList == null || resultsList.Count == 0)
                    return;

                var steamId = LocalSteamId.Get();
                if (steamId == 0)
                    return;

                // Client waiting for host sync: fail closed to builtins only (not full Workshop list).
                var waitingForHost = AllowlistStore.ExpectHostAllowlist && !AllowlistStore.HasHostAllowlist;

                // If none of this airframe's options are squadron-assigned, show vanilla builtins.
                var hasSquadronSkinForAirframe = !waitingForHost
                    && AllowlistStore.Data.OptionsContainExplicitAllow(steamId, resultsList);
                if (!waitingForHost)
                {
                    var defName = aircraft != null ? aircraft.name : null;
                    AllowlistStore.CacheAirframeMode(steamId, defName, hasSquadronSkinForAirframe);
                }

                for (var i = resultsList.Count - 1; i >= 0; i--)
                {
                    var key = resultsList[i].Item1;
                    bool allowed;
                    if (waitingForHost)
                    {
                        allowed = key.Type == LiveryKey.KeyType.Builtin;
                    }
                    else if (!hasSquadronSkinForAirframe)
                    {
                        allowed = key.Type == LiveryKey.KeyType.Builtin;
                    }
                    else
                    {
                        allowed = AllowlistStore.IsAllowed(steamId, key);
                    }

                    if (!allowed)
                    {
                        resultsList.RemoveAt(i);
                        continue;
                    }

                    if (waitingForHost || !hasSquadronSkinForAirframe)
                        continue;

                    var customLabel = AllowlistStore.Data.GetCustomLabel(steamId, key);
                    if (!string.IsNullOrWhiteSpace(customLabel))
                        resultsList[i] = new ValueTuple<LiveryKey, string>(key, customLabel);
                }

                if (resultsList.Count == 0)
                {
                    // Prefer a builtin that was on the original airframe list; never inject a wrong Workshop key.
                    resultsList.Add(new ValueTuple<LiveryKey, string>(new LiveryKey(0), "Default"));
                    SkinGatePlugin.Log?.LogWarning(
                        $"SkinGate UI filter left 0 options for SteamID {steamId}; using builtin 0.");
                }
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogError($"GetLiveryOptions filter failed: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SetLiveryKey))]
    public static class Patch_SetLiveryKey
    {
        [HarmonyPrefix]
        public static bool Prefix(Aircraft __instance, ref LiveryKey liveryKey, bool loadIfUnspawned)
        {
            try
            {
                if (!LiveryEnforcer.ShouldEnforceNetworkSync())
                    return true;

                var player = __instance != null ? __instance.Player : null;
                if (player == null)
                    return true; // AI / no human owner — out of scope for v1

                var steamId = player.SteamID != 0 ? player.SteamID : player.CSteamID.m_SteamID;
                if (steamId == 0)
                    return true;

                return LiveryEnforcer.SanitizeForPlayer(steamId, ref liveryKey, loadIfUnspawned, __instance);
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogError($"SetLiveryKey gate failed: {ex}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Aircraft), "set_NetworkLiveryKey")]
    public static class Patch_NetworkLiveryKey
    {
        [HarmonyPrefix]
        public static void Prefix(Aircraft __instance, ref LiveryKey value)
        {
            try
            {
                if (!LiveryEnforcer.ShouldEnforceNetworkSync())
                    return;

                var player = __instance != null ? __instance.Player : null;
                if (player == null)
                    return;

                var steamId = player.SteamID != 0 ? player.SteamID : player.CSteamID.m_SteamID;
                if (steamId == 0)
                    return;

                // Server is authoritative for what gets synced to other clients.
                var onServer = NetworkAuthority.IsServerActive;
                var isLocalOwner = player.IsLocalPlayer;
                if (!onServer && !isLocalOwner)
                    return;

                LiveryEnforcer.SanitizeForPlayer(steamId, ref value, hangarPreview: false, __instance);
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogError($"NetworkLiveryKey gate failed: {ex}");
            }
        }
    }
}
