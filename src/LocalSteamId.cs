using System;
using Steamworks;
using UnityEngine;

namespace SkinGate
{
    /// <summary>
    /// Resolves the local player's SteamID64 for hangar UI (Player.IsLocalPlayer is often not ready yet).
    /// </summary>
    public static class LocalSteamId
    {
        private static ulong _cached;
        private static float _cachedAtUnscaled = -999f;
        private static bool _warnedUnresolved;

        public static ulong Get()
        {
            if (_cached != 0 && Time.unscaledTime - _cachedAtUnscaled < 2f)
                return _cached;

            var id = ResolveFromNetworkPlayer();
            if (id == 0)
                id = ResolveFromSteamworks();

            if (id != 0)
            {
                _cached = id;
                _cachedAtUnscaled = Time.unscaledTime;
                return id;
            }

            if (!_warnedUnresolved)
            {
                _warnedUnresolved = true;
                SkinGatePlugin.Log?.LogWarning(
                    "SkinGate could not resolve local SteamID — livery UI will not filter until a Player exists. " +
                    "Install SkinGate on every PC that should see squad skin restrictions.");
            }

            return 0;
        }

        private static ulong ResolveFromNetworkPlayer()
        {
            try
            {
                var players = UnityEngine.Object.FindObjectsOfType<NuclearOption.Networking.Player>();
                NuclearOption.Networking.Player local = null;
                foreach (var p in players)
                {
                    if (p == null)
                        continue;
                    if (p.IsLocalPlayer)
                    {
                        local = p;
                        break;
                    }
                }

                if (local == null)
                    return 0;

                if (local.SteamID != 0)
                    return local.SteamID;

                if (local.CSteamID.m_SteamID != 0)
                    return local.CSteamID.m_SteamID;
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogDebug($"LocalSteamId Player scan failed: {ex.Message}");
            }

            return 0;
        }

        private static ulong ResolveFromSteamworks()
        {
            try
            {
                if (!SteamAPI.IsSteamRunning())
                    return 0;
                return SteamUser.GetSteamID().m_SteamID;
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogDebug($"LocalSteamId Steamworks failed: {ex.Message}");
                return 0;
            }
        }
    }
}
