using System;
using System.Security.Cryptography;
using System.Text;
using Mirage;
using Mirage.Serialization;
using UnityEngine;

namespace SkinGate
{
    public struct SkinGateAllowlistRequestMessage { }

    public struct SkinGateAllowlistMessage
    {
        public string Json;
        public string Hash;
        public int PlayerCount;
    }

    public struct SkinGateAllowlistAckMessage
    {
        public string Hash;
        public ulong SteamId;
    }

    /// <summary>
    /// Host reads local allowlist.json; clients request/receive it over Mirage with ACK + retry.
    /// </summary>
    public sealed class AllowlistNetworkSync : MonoBehaviour
    {
        private const float HookRetrySeconds = 1f;
        private const float ClientRequestInterval = 2f;
        private const float HostPushDelaySeconds = 0.75f;
        private const int MaxClientRequests = 30;

        private bool _messagesRegistered;
        private bool _serverHooked;
        private bool _clientHooked;
        private float _nextHookAttempt;
        private float _nextClientRequest;
        private int _clientRequestCount;
        private NetworkServer _server;
        private NetworkClient _client;

        private void Update()
        {
            if (Time.unscaledTime >= _nextHookAttempt)
            {
                _nextHookAttempt = Time.unscaledTime + HookRetrySeconds;
                TryRegisterMessages();
                TryHookServer();
                TryHookClient();
            }

            if (_clientHooked && _client != null && _client.Active && !_client.IsHost)
            {
                if (!AllowlistStore.HasHostAllowlist
                    && _clientRequestCount < MaxClientRequests
                    && Time.unscaledTime >= _nextClientRequest)
                {
                    _nextClientRequest = Time.unscaledTime + ClientRequestInterval;
                    RequestAllowlistFromHost();
                }
            }
        }

        private void OnDestroy()
        {
            if (_server != null)
            {
                _server.Connected.RemoveListener(OnServerPlayerConnected);
                _server.Authenticated.RemoveListener(OnServerPlayerAuthenticated);
            }
        }

        private void TryRegisterMessages()
        {
            if (_messagesRegistered)
                return;
            try
            {
                MessagePacker.RegisterMessage<SkinGateAllowlistRequestMessage>();
                MessagePacker.RegisterMessage<SkinGateAllowlistMessage>();
                MessagePacker.RegisterMessage<SkinGateAllowlistAckMessage>();
                _messagesRegistered = true;
                SkinGatePlugin.Log?.LogInfo("SkinGate network messages registered.");
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate message registration failed: {ex.Message}");
            }
        }

        private void TryHookServer()
        {
            if (_serverHooked)
                return;

            var server = UnityEngine.Object.FindObjectOfType<NetworkServer>();
            if (server == null || !server.Active)
                return;

            _server = server;
            server.Connected.AddListener(OnServerPlayerConnected);
            server.Authenticated.AddListener(OnServerPlayerAuthenticated);
            server.MessageHandler.RegisterHandler<SkinGateAllowlistRequestMessage>(OnAllowlistRequested, false);
            server.MessageHandler.RegisterHandler<SkinGateAllowlistAckMessage>(OnAllowlistAck, false);
            _serverHooked = true;
            SkinGatePlugin.Log?.LogInfo("SkinGate host sync active (allowlist from host file).");
        }

        private void TryHookClient()
        {
            if (_clientHooked)
                return;

            var client = UnityEngine.Object.FindObjectOfType<NetworkClient>();
            if (client == null || !client.Active)
                return;

            _client = client;
            client.MessageHandler.RegisterHandler<SkinGateAllowlistMessage>(OnAllowlistReceived, false);
            _clientHooked = true;

            if (!client.IsHost)
            {
                AllowlistStore.SetExpectHostAllowlist(true);
                SkinGatePlugin.Log?.LogInfo("SkinGate client sync active — waiting for host allowlist.");
                _nextClientRequest = Time.unscaledTime + 0.5f;
            }
        }

        private void OnServerPlayerConnected(INetworkPlayer player)
        {
            // Backup push; primary is Authenticated after a short delay.
            ScheduleSend(player);
        }

        private void OnServerPlayerAuthenticated(INetworkPlayer player)
        {
            ScheduleSend(player);
        }

        private void ScheduleSend(INetworkPlayer player)
        {
            StartCoroutine(SendAfterDelay(player, HostPushDelaySeconds));
        }

        private System.Collections.IEnumerator SendAfterDelay(INetworkPlayer player, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            TrySendAllowlistTo(player);
        }

        private void OnAllowlistRequested(INetworkPlayer player, SkinGateAllowlistRequestMessage _)
        {
            SkinGatePlugin.Log?.LogInfo("SkinGate SYNC request received from a client.");
            TrySendAllowlistTo(player);
        }

        private void OnAllowlistAck(INetworkPlayer player, SkinGateAllowlistAckMessage ack)
        {
            SkinGatePlugin.Log?.LogInfo(
                $"SkinGate SYNC ack hash={ack.Hash} steamId={ack.SteamId}");
        }

        private void OnAllowlistReceived(INetworkPlayer _, SkinGateAllowlistMessage message)
        {
            if (string.IsNullOrEmpty(message.Json))
            {
                SkinGatePlugin.Log?.LogWarning("SkinGate SYNC recv empty payload.");
                return;
            }

            var hash = string.IsNullOrEmpty(message.Hash) ? ShortHash(message.Json) : message.Hash;
            SkinGatePlugin.Log?.LogInfo(
                $"SkinGate SYNC recv bytes={Encoding.UTF8.GetByteCount(message.Json)} hash={hash} players={message.PlayerCount}");

            AllowlistStore.ApplyHostJson(message.Json);
            SendAck(hash);
        }

        private void SendAck(string hash)
        {
            try
            {
                if (_client == null || !_client.Active || _client.IsHost)
                    return;

                var ack = new SkinGateAllowlistAckMessage
                {
                    Hash = hash ?? "",
                    SteamId = LocalSteamId.Get(),
                };
                _client.Send(ack, channelId: Channel.Reliable);
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate SYNC ack send failed: {ex.Message}");
            }
        }

        private void RequestAllowlistFromHost()
        {
            try
            {
                if (_client == null || !_client.Active || _client.IsHost)
                    return;

                _clientRequestCount += 1;
                SkinGatePlugin.Log?.LogInfo(
                    $"SkinGate SYNC request #{_clientRequestCount} to host…");
                _client.Send(new SkinGateAllowlistRequestMessage(), channelId: Channel.Reliable);
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate SYNC request failed: {ex.Message}");
            }
        }

        public static void HostBroadcastAllowlist()
        {
            var sync = SkinGatePlugin.Instance?.GetComponent<AllowlistNetworkSync>();
            sync?.BroadcastToAllConnectedClients();
        }

        private void BroadcastToAllConnectedClients()
        {
            if (_server == null || !_server.Active)
                return;

            if (!TryBuildPayload(out var message))
                return;

            try
            {
                _server.SendToAll(message, true, false, Channel.Reliable);
                SkinGatePlugin.Log?.LogInfo(
                    $"SkinGate SYNC broadcast bytes={Encoding.UTF8.GetByteCount(message.Json)} hash={message.Hash} players={message.PlayerCount} to {_server.AuthenticatedPlayers.Count} connection(s).");
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate SYNC broadcast failed: {ex.Message}");
            }
        }

        private void TrySendAllowlistTo(INetworkPlayer player)
        {
            if (_server == null || !_server.Active || player == null)
                return;

            if (!TryBuildPayload(out var message))
                return;

            try
            {
                _server.SendToMany(new[] { player }, message, true, channelId: Channel.Reliable);
                SkinGatePlugin.Log?.LogInfo(
                    $"SkinGate SYNC send bytes={Encoding.UTF8.GetByteCount(message.Json)} hash={message.Hash} players={message.PlayerCount}");
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate SYNC send failed: {ex.Message}");
            }
        }

        private static bool TryBuildPayload(out SkinGateAllowlistMessage message)
        {
            message = default;
            try
            {
                var path = SkinGatePlugin.ResolveAllowlistPath(SkinGatePlugin.AllowlistPath.Value);
                if (!System.IO.File.Exists(path))
                {
                    SkinGatePlugin.Log?.LogWarning($"SkinGate SYNC host allowlist missing: {path}");
                    return false;
                }

                var json = System.IO.File.ReadAllText(path);
                var hash = ShortHash(json);
                message = new SkinGateAllowlistMessage
                {
                    Json = json,
                    Hash = hash,
                    PlayerCount = AllowlistStore.Data?.Players?.Count ?? 0,
                };
                return true;
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate SYNC read host allowlist failed: {ex.Message}");
                return false;
            }
        }

        internal static string ShortHash(string text)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 4 && i < bytes.Length; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }

    public static class AllowlistSession
    {
        public static bool IsHostAllowlistSource
        {
            get
            {
                var server = UnityEngine.Object.FindObjectOfType<NetworkServer>();
                if (server != null && server.Active)
                    return true;

                var client = UnityEngine.Object.FindObjectOfType<NetworkClient>();
                if (client != null && client.IsConnected && !client.IsHost)
                    return false;

                return true;
            }
        }
    }
}
