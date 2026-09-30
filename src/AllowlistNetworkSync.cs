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
    /// BepInEx plugins are not Mirage-weaved, so register readers/writers at runtime.
    /// Without this, clients stay fail-closed on vanilla skins ("No writer found…").
    /// </summary>
    internal static class SkinGateMessageSerializers
    {
        public static void EnsureRegistered()
        {
            // Always re-assign: Mirage clears Writer/Reader delegates across scene / net restarts.
            Writer<SkinGateAllowlistRequestMessage>.Write = WriteRequest;
            Reader<SkinGateAllowlistRequestMessage>.Read = ReadRequest;

            Writer<SkinGateAllowlistMessage>.Write = WriteAllowlist;
            Reader<SkinGateAllowlistMessage>.Read = ReadAllowlist;

            Writer<SkinGateAllowlistAckMessage>.Write = WriteAck;
            Reader<SkinGateAllowlistAckMessage>.Read = ReadAck;
        }

        private static void WriteRequest(NetworkWriter writer, SkinGateAllowlistRequestMessage _)
        {
            // empty payload
        }

        private static SkinGateAllowlistRequestMessage ReadRequest(NetworkReader reader)
        {
            return default;
        }

        private static void WriteAllowlist(NetworkWriter writer, SkinGateAllowlistMessage message)
        {
            var json = message.Json ?? "";
            var bytes = Encoding.UTF8.GetBytes(json);
            writer.WriteInt32(bytes.Length);
            if (bytes.Length > 0)
                writer.WriteBytes(bytes, 0, bytes.Length);
            writer.WriteString(message.Hash ?? "");
            writer.WriteInt32(message.PlayerCount);
        }

        private static SkinGateAllowlistMessage ReadAllowlist(NetworkReader reader)
        {
            try
            {
                var len = reader.ReadInt32();
                string json;
                if (len <= 0 || len > 2_000_000)
                {
                    json = "";
                }
                else
                {
                    var buf = new byte[len];
                    reader.ReadBytes(buf, 0, len);
                    json = Encoding.UTF8.GetString(buf);
                }

                return new SkinGateAllowlistMessage
                {
                    Json = json,
                    Hash = reader.ReadString() ?? "",
                    PlayerCount = reader.ReadInt32(),
                };
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate allowlist deserialize failed: {ex.Message}");
                return default;
            }
        }

        private static void WriteAck(NetworkWriter writer, SkinGateAllowlistAckMessage message)
        {
            writer.WriteString(message.Hash ?? "");
            writer.WriteUInt64(message.SteamId);
        }

        private static SkinGateAllowlistAckMessage ReadAck(NetworkReader reader)
        {
            return new SkinGateAllowlistAckMessage
            {
                Hash = reader.ReadString() ?? "",
                SteamId = reader.ReadUInt64(),
            };
        }
    }

    /// <summary>
    /// Host reads local allowlist.json; clients request/receive it over Mirage with ACK + retry.
    /// </summary>
    public sealed class AllowlistNetworkSync : MonoBehaviour
    {
        private const float HookRetrySeconds = 0.5f;
        private const float ClientRequestInterval = 1.5f;
        private const float HostBackupPushDelaySeconds = 3.5f;
        private const int MaxClientRequests = 40;

        private bool _messagesRegistered;
        private bool _serverHooked;
        private bool _clientHooked;
        private float _nextHookAttempt;
        private float _nextClientRequest;
        private int _clientRequestCount;
        private NetworkServer _server;
        private NetworkClient _client;

        private void Awake()
        {
            // Register before any multiplayer connect — late registration causes
            // Mirage to drop joiners with "local client stopped".
            TryRegisterMessages();
        }

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
                _server.Authenticated.RemoveListener(OnServerPlayerAuthenticated);
        }

        private void TryRegisterMessages()
        {
            try
            {
                // Re-bind writers every tick attempt — Mirage drops them across net restarts.
                SkinGateMessageSerializers.EnsureRegistered();
                if (_messagesRegistered)
                    return;

                MessagePacker.RegisterMessage<SkinGateAllowlistRequestMessage>();
                MessagePacker.RegisterMessage<SkinGateAllowlistMessage>();
                MessagePacker.RegisterMessage<SkinGateAllowlistAckMessage>();
                _messagesRegistered = true;
                SkinGatePlugin.Log?.LogInfo("SkinGate network messages registered (with runtime serializers).");
            }
            catch (Exception ex)
            {
                SkinGatePlugin.Log?.LogWarning($"SkinGate message registration failed: {ex.Message}");
            }
        }

        private void TryHookServer()
        {
            var server = UnityEngine.Object.FindObjectOfType<NetworkServer>();
            if (server == null || !server.Active)
            {
                if (_serverHooked)
                {
                    if (_server != null)
                        _server.Authenticated.RemoveListener(OnServerPlayerAuthenticated);
                    _serverHooked = false;
                    _server = null;
                }
                return;
            }

            if (_serverHooked && ReferenceEquals(_server, server))
                return;

            if (_server != null)
                _server.Authenticated.RemoveListener(OnServerPlayerAuthenticated);

            SkinGateMessageSerializers.EnsureRegistered();
            _server = server;
            // Do NOT push on Connected — client often has no readers yet and Mirage drops them.
            server.Authenticated.AddListener(OnServerPlayerAuthenticated);
            server.MessageHandler.RegisterHandler<SkinGateAllowlistRequestMessage>(OnAllowlistRequested, false);
            server.MessageHandler.RegisterHandler<SkinGateAllowlistAckMessage>(OnAllowlistAck, false);
            _serverHooked = true;
            SkinGatePlugin.Log?.LogInfo("SkinGate host sync active (request-driven + delayed backup).");
        }

        private void TryHookClient()
        {
            var client = UnityEngine.Object.FindObjectOfType<NetworkClient>();
            if (client == null || !client.Active)
            {
                // Disconnected: allow a fresh request cycle on the next session.
                if (_clientHooked)
                {
                    _clientHooked = false;
                    _client = null;
                    _clientRequestCount = 0;
                    if (!AllowlistSession.IsHostAllowlistSource)
                        AllowlistStore.InvalidateHostAllowlist();
                }
                return;
            }

            if (_clientHooked && ReferenceEquals(_client, client))
                return;

            SkinGateMessageSerializers.EnsureRegistered();
            _client = client;
            client.MessageHandler.RegisterHandler<SkinGateAllowlistMessage>(OnAllowlistReceived, false);
            _clientHooked = true;

            if (!client.IsHost)
            {
                // Force a new pull every join so squadron role changes apply.
                AllowlistStore.InvalidateHostAllowlist();
                AllowlistStore.SetExpectHostAllowlist(true);
                _clientRequestCount = 0;
                SkinGatePlugin.Log?.LogInfo("SkinGate client sync active — waiting for host allowlist.");
                _nextClientRequest = Time.unscaledTime + 0.5f;
            }
        }

        private void OnServerPlayerAuthenticated(INetworkPlayer player)
        {
            // Backup only — primary path is client REQUEST after its readers are ready.
            ScheduleSend(player);
        }

        private void ScheduleSend(INetworkPlayer player)
        {
            StartCoroutine(SendAfterDelay(player, HostBackupPushDelaySeconds));
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

                SkinGateMessageSerializers.EnsureRegistered();
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

                SkinGateMessageSerializers.EnsureRegistered();
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

            SkinGateMessageSerializers.EnsureRegistered();
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

            SkinGateMessageSerializers.EnsureRegistered();
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
