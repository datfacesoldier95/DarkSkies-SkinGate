using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mirage;
using Steamworks;
using UnityEngine;

namespace SkinGate
{
    /// <summary>
    /// Host → joiner allowlist sync over Steam NetworkingMessages (not Mirage).
    /// Mirage custom messages caused "Local Client Stopped" on join.
    /// </summary>
    public sealed class AllowlistSteamSync : MonoBehaviour
    {
        private const int Channel = 7781;
        private const byte ProtocolVersion = 1;
        private const byte MsgRequest = 1;
        private const byte MsgPayload = 2;
        private const byte MsgAck = 3;
        private static readonly byte[] Magic = { (byte)'S', (byte)'G', (byte)'A', (byte)'L' };

        private const float TickSeconds = 0.5f;
        private const float HostPushInterval = 2.5f;
        private const float ClientRequestInterval = 1.5f;
        private const int MaxClientRequests = 60;

        private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
        private float _nextTick;
        private float _nextClientRequest;
        private int _clientRequestCount;
        private bool _wasClientNonHost;
        private readonly Dictionary<ulong, float> _nextHostPush = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> _ackedPeers = new HashSet<ulong>();
        private string _lastBroadcastHash;

        private void Awake()
        {
            try
            {
                _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
                SkinGatePlugin.Log?.LogInfo("SkinGate Steam allowlist sync ready (NetworkingMessages).");
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate Steam sync callback failed: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            _sessionRequest?.Dispose();
            _sessionRequest = null;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextTick)
                return;
            _nextTick = Time.unscaledTime + TickSeconds;

            try
            {
                if (!SteamAPI.IsSteamRunning())
                    return;

                PumpIncoming();
                UpdateRoleAndExchange();
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate Steam sync tick failed: {ex.Message}");
            }
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t req)
        {
            try
            {
                var identity = req.m_identityRemote;
                SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate Steam AcceptSession failed: {ex.Message}");
            }
        }

        private void UpdateRoleAndExchange()
        {
            var isHostSource = AllowlistSession.IsHostAllowlistSource;
            var isClientNonHost = AllowlistSession.IsClientNonHost;

            if (isClientNonHost && !_wasClientNonHost)
            {
                // Fresh join: drop any local/stale allowlist until host payload arrives.
                AllowlistStore.InvalidateHostAllowlist();
                AllowlistStore.SetExpectHostAllowlist(true);
                _clientRequestCount = 0;
                _nextClientRequest = Time.unscaledTime + 0.25f;
                SkinGatePlugin.Log?.LogInfo("SkinGate client mode — waiting for host allowlist over Steam.");
            }
            else if (!isClientNonHost && _wasClientNonHost)
            {
                AllowlistStore.SetExpectHostAllowlist(false);
                _clientRequestCount = 0;
                _ackedPeers.Clear();
            }

            _wasClientNonHost = isClientNonHost;

            if (isHostSource)
            {
                HostPushToRemotes();
            }
            else if (isClientNonHost && !AllowlistStore.HasHostAllowlist)
            {
                if (_clientRequestCount < MaxClientRequests && Time.unscaledTime >= _nextClientRequest)
                {
                    _nextClientRequest = Time.unscaledTime + ClientRequestInterval;
                    ClientRequestFromRemotes();
                }
            }
        }

        private void HostPushToRemotes()
        {
            if (!TryBuildPayload(out var payloadBytes, out var hash))
                return;

            foreach (var peer in EnumerateRemoteSteamIds())
            {
                if (_ackedPeers.Contains(peer))
                    continue;

                if (_nextHostPush.TryGetValue(peer, out var next) && Time.unscaledTime < next)
                    continue;

                _nextHostPush[peer] = Time.unscaledTime + HostPushInterval;
                if (SendRaw(peer, payloadBytes))
                {
                    SkinGatePlugin.Log?.LogInfo(
                        $"SkinGate STEAM send allowlist hash={hash} to {peer}");
                }
            }
        }

        private void ClientRequestFromRemotes()
        {
            var req = BuildRequest();
            var any = false;
            foreach (var peer in EnumerateRemoteSteamIds())
            {
                if (SendRaw(peer, req))
                    any = true;
            }

            if (any)
            {
                _clientRequestCount += 1;
                SkinGatePlugin.Log?.LogInfo($"SkinGate STEAM request #{_clientRequestCount} to host peers…");
            }
        }

        /// <summary>Host: push current file allowlist to all remote players (after F11 /sync).</summary>
        public static void HostBroadcastAllowlist()
        {
            var sync = SkinGatePlugin.Instance?.GetComponent<AllowlistSteamSync>();
            sync?.BroadcastNow();
        }

        private void BroadcastNow()
        {
            if (!AllowlistSession.IsHostAllowlistSource)
                return;

            _ackedPeers.Clear();
            _nextHostPush.Clear();

            if (!TryBuildPayload(out var payloadBytes, out var hash))
                return;

            _lastBroadcastHash = hash;
            var sent = 0;
            foreach (var peer in EnumerateRemoteSteamIds())
            {
                if (SendRaw(peer, payloadBytes))
                    sent += 1;
            }

            SkinGatePlugin.Log?.LogInfo(
                $"SkinGate STEAM broadcast hash={hash} to {sent} peer(s).");
        }

        private void PumpIncoming()
        {
            var ptrs = new IntPtr[32];
            int count;
            try
            {
                count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, ptrs, ptrs.Length);
            }
            catch
            {
                return;
            }

            for (var i = 0; i < count; i++)
            {
                try
                {
                    var msg = SteamNetworkingMessage_t.FromIntPtr(ptrs[i]);
                    var peer = msg.m_identityPeer.GetSteamID64();
                    if (msg.m_pData != IntPtr.Zero && msg.m_cbSize > 0)
                    {
                        var bytes = new byte[msg.m_cbSize];
                        Marshal.Copy(msg.m_pData, bytes, 0, msg.m_cbSize);
                        HandlePacket(peer, bytes);
                    }
                }
                catch (Exception ex)
                {
                    SkinGatePlugin.Log?.LogWarning($"SkinGate STEAM recv handle failed: {ex.Message}");
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(ptrs[i]);
                }
            }
        }

        private void HandlePacket(ulong peer, byte[] bytes)
        {
            if (bytes == null || bytes.Length < 6)
                return;
            if (bytes[0] != Magic[0] || bytes[1] != Magic[1] || bytes[2] != Magic[2] || bytes[3] != Magic[3])
                return;
            if (bytes[4] != ProtocolVersion)
                return;

            var type = bytes[5];
            if (type == MsgRequest)
            {
                if (!AllowlistSession.IsHostAllowlistSource)
                    return;
                if (TryBuildPayload(out var payload, out var hash))
                {
                    SendRaw(peer, payload);
                    SkinGatePlugin.Log?.LogInfo($"SkinGate STEAM request from {peer} → sent hash={hash}");
                }
                return;
            }

            if (type == MsgAck)
            {
                if (bytes.Length < 10)
                    return;
                var hash = Encoding.ASCII.GetString(bytes, 6, 4);
                _ackedPeers.Add(peer);
                SkinGatePlugin.Log?.LogInfo($"SkinGate STEAM ack from {peer} hash={hash}");
                return;
            }

            if (type == MsgPayload)
            {
                if (AllowlistSession.IsHostAllowlistSource)
                    return;
                if (bytes.Length < 14)
                    return;

                var hash = Encoding.ASCII.GetString(bytes, 6, 4);
                var jsonLen = BitConverter.ToInt32(bytes, 10);
                if (jsonLen <= 0 || jsonLen > 2_000_000 || 14 + jsonLen > bytes.Length)
                {
                    SkinGatePlugin.Log?.LogWarning("SkinGate STEAM payload bad length.");
                    return;
                }

                var json = Encoding.UTF8.GetString(bytes, 14, jsonLen);
                AllowlistStore.ApplyHostJson(json);
                AllowlistStore.SetExpectHostAllowlist(false);
                SkinGatePlugin.Log?.LogInfo(
                    $"SkinGate STEAM recv allowlist hash={hash} bytes={jsonLen} from {peer}");
                SendRaw(peer, BuildAck(hash));
            }
        }

        private static bool SendRaw(ulong steamId, byte[] data)
        {
            if (steamId == 0 || data == null || data.Length == 0)
                return false;

            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID64(steamId);
                var result = SteamNetworkingMessages.SendMessageToUser(
                    ref identity,
                    handle.AddrOfPinnedObject(),
                    (uint)data.Length,
                    Constants.k_nSteamNetworkingSend_Reliable
                    | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession,
                    Channel);
                return result == EResult.k_EResultOK || result == EResult.k_EResultIgnored;
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate STEAM send to {steamId} failed: {ex.Message}");
                return false;
            }
            finally
            {
                handle.Free();
            }
        }

        private static byte[] BuildRequest()
        {
            return new byte[]
            {
                Magic[0], Magic[1], Magic[2], Magic[3],
                ProtocolVersion,
                MsgRequest,
            };
        }

        private static byte[] BuildAck(string hash4)
        {
            var h = Encoding.ASCII.GetBytes((hash4 ?? "0000").PadRight(4).Substring(0, 4));
            var buf = new byte[10];
            buf[0] = Magic[0];
            buf[1] = Magic[1];
            buf[2] = Magic[2];
            buf[3] = Magic[3];
            buf[4] = ProtocolVersion;
            buf[5] = MsgAck;
            Buffer.BlockCopy(h, 0, buf, 6, 4);
            return buf;
        }

        private static bool TryBuildPayload(out byte[] packet, out string hash)
        {
            packet = null;
            hash = null;
            try
            {
                var path = SkinGatePlugin.ResolveAllowlistPath(SkinGatePlugin.AllowlistPath.Value);
                if (!File.Exists(path))
                {
                    SkinGatePlugin.Log?.LogWarning($"SkinGate STEAM host allowlist missing: {path}");
                    return false;
                }

                var json = File.ReadAllText(path);
                hash = ShortHash(json);
                var jsonBytes = Encoding.UTF8.GetBytes(json ?? "");
                var hashBytes = Encoding.ASCII.GetBytes(hash);
                packet = new byte[14 + jsonBytes.Length];
                packet[0] = Magic[0];
                packet[1] = Magic[1];
                packet[2] = Magic[2];
                packet[3] = Magic[3];
                packet[4] = ProtocolVersion;
                packet[5] = MsgPayload;
                Buffer.BlockCopy(hashBytes, 0, packet, 6, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(jsonBytes.Length), 0, packet, 10, 4);
                Buffer.BlockCopy(jsonBytes, 0, packet, 14, jsonBytes.Length);
                return true;
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate STEAM build payload failed: {ex.Message}");
                return false;
            }
        }

        internal static string ShortHash(string text)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(8);
                for (var i = 0; i < 4 && i < bytes.Length; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString().Substring(0, 4);
            }
        }

        private static IEnumerable<ulong> EnumerateRemoteSteamIds()
        {
            var local = LocalSteamId.Get();
            var seen = new HashSet<ulong>();
            NuclearOption.Networking.Player[] players = null;
            try
            {
                players = UnityEngine.Object.FindObjectsOfType<NuclearOption.Networking.Player>();
            }
            catch
            {
                yield break;
            }

            if (players == null)
                yield break;

            foreach (var p in players)
            {
                if (p == null || p.IsLocalPlayer)
                    continue;

                ulong id = 0;
                try
                {
                    id = p.SteamID != 0 ? p.SteamID : p.CSteamID.m_SteamID;
                }
                catch
                {
                    continue;
                }

                if (id == 0 || id == local || !seen.Add(id))
                    continue;

                yield return id;
            }
        }
    }

    public static class AllowlistSession
    {
        public static bool IsHostAllowlistSource
        {
            get
            {
                try
                {
                    var server = UnityEngine.Object.FindObjectOfType<NetworkServer>();
                    if (server != null && server.Active)
                        return true;

                    var client = UnityEngine.Object.FindObjectOfType<NetworkClient>();
                    if (client != null && client.IsConnected && !client.IsHost)
                        return false;
                }
                catch
                {
                    /* ignore */
                }

                return true;
            }
        }

        public static bool IsClientNonHost
        {
            get
            {
                try
                {
                    var client = UnityEngine.Object.FindObjectOfType<NetworkClient>();
                    return client != null && client.IsConnected && !client.IsHost;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
