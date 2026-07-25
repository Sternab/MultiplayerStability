// Low-level Steam networking transport. This is the only file that touches the Steamworks API, keeping
// the rest of the mod independent of Steamworks types. Everything here runs on
// the Unity main thread: the receive pump lives in a mod MonoBehaviour's Update, and Steam's session
// callbacks fire inside the game's own SteamAPI.RunCallbacks (SteamManager.Update) which is main-thread.
//
// Reliable ordered messaging uses one dedicated channel. Session requests are accepted only from
// expected Steam IDs. The game owns SteamAPI.RunCallbacks; this component does not pump it.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace MultiplayerStability
{
    internal static class SteamP2P
    {
        internal const int Channel = 47;   // arbitrary; no game code uses SteamNetworkingMessages at all

        // k_nSteamNetworkingSend_Reliable(8) | _AutoRestartBrokenSession(32); verified in Constants.cs.
        private const int SendReliable = 8 | 32;

        private static bool s_available;
        private static DateTime s_nextAvailabilityProbeUtc;
        private static bool s_loggedUnavailable;
        private static ulong s_localSteamId;
        private static Callback<SteamNetworkingMessagesSessionRequest_t> s_sessionReq;
        private static Callback<SteamNetworkingMessagesSessionFailed_t> s_sessionFailed;
        private static readonly HashSet<ulong> s_acceptFrom = new HashSet<ulong>();
        private static SteamP2PPump s_pump;
        private static readonly IntPtr[] s_recvBuf = new IntPtr[64];

        // Set by SteamSaveTransfer: (fromSteamId, message bytes). Invoked on the main thread by the pump.
        internal static Action<ulong, byte[]> MessageHandler;
        // Set by SteamSaveTransfer: a peer's session dropped (steamId). Triggers fallback.
        internal static Action<ulong> SessionFailed;

        internal static ulong LocalSteamId => s_localSteamId;

        // True only on an initialized Steam client. A probe made before Steam finishes initializing is
        // retried after five seconds instead of permanently disabling the transport for this process.
        internal static bool Available
        {
            get
            {
                if (s_available)
                    return true;
                if (DateTime.UtcNow < s_nextAvailabilityProbeUtc)
                    return false;
                s_nextAvailabilityProbeUtc = DateTime.UtcNow.AddSeconds(5);
                try
                {
                    // GetSteamID throws (InteropHelp.TestIfAvailableClient) if SteamAPI isn't initialized.
                    s_localSteamId = SteamUser.GetSteamID().m_SteamID;
                    s_available = s_localSteamId != 0UL;
                    if (s_available)
                        s_loggedUnavailable = false;
                }
                catch (Exception e)
                {
                    s_available = false;
                    if (!s_loggedUnavailable)
                    {
                        s_loggedUnavailable = true;
                        MultiplayerStabilityMain.LogNoThrow(
                            "[SteamP2P] Steam unavailable; will retry and use Photon meanwhile: " + e.Message);
                    }
                }
                return s_available;
            }
        }

        // Registers session callbacks, warms the relay network, and starts the receive pump. Idempotent.
        internal static void EnsureInit()
        {
            if (!Available || s_pump != null)
                return;
            if (s_sessionReq == null)
                s_sessionReq = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            if (s_sessionFailed == null)
                s_sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            // Valve's transport sends at a FIXED rate = clamp(256KB/s, SendRateMin, SendRateMax); upward
            // bandwidth probing is not implemented (proven by field tests 2-5: the live rate always equals
            // the floor, and a high floor causes loss without backoff). The transfer code adapts this
            // value through SetSendRateFloor while monitoring delivery.
            SetSendRateFloor(1024 * 1024);
            SetGlobalConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 64 * 1024 * 1024);
            SetGlobalConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4 * 1024 * 1024);
            // Allow direct peer-to-peer (NAT punch) instead of forcing everything through Valve's SDR relay;
            // the telemetry's "via [...]" shows which transport the session actually negotiated.
            SetGlobalConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_All);
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
            catch (Exception e) { MultiplayerStabilityMain.LogNoThrow("[SteamP2P] InitRelayNetworkAccess: " + e.Message); }
            if (s_pump == null)
            {
                var go = new GameObject("MPStability_SteamPump");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                s_pump = go.AddComponent<SteamP2PPump>();
            }
            MultiplayerStabilityMain.LogNoThrow("[SteamP2P] Initialized. LocalSteamId=" + s_localSteamId);
        }

        internal static void AllowSessionFrom(ulong steamId) => s_acceptFrom.Add(steamId);
        internal static void DisallowSessionFrom(ulong steamId) => s_acceptFrom.Remove(steamId);

        // Sends one framed message reliably. Returns the Steam EResult (OK / LimitExceeded / NoConnection...).
        internal static EResult Send(ulong peerSteamId, byte[] frame, int len)
        {
            var id = default(SteamNetworkingIdentity);
            id.SetSteamID64(peerSteamId);
            var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                // Steam copies the payload synchronously, so freeing the pin right after returns is safe.
                return SteamNetworkingMessages.SendMessageToUser(ref id, handle.AddrOfPinnedObject(), (uint)len, SendReliable, Channel);
            }
            finally
            {
                handle.Free();
            }
        }

        private static bool SetGlobalConfigInt(ESteamNetworkingConfigValue key, int value)
        {
            var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                bool ok = SteamNetworkingUtils.SetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, handle.AddrOfPinnedObject());
                MultiplayerStabilityMain.LogNoThrow("[SteamP2P] Config " + key + "=" + value + " ok=" + ok);
                return ok;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow("[SteamP2P][ERR] SetConfigValue " + key + ": " + e.Message);
                return false;
            }
            finally
            {
                handle.Free();
            }
        }

        // Live transport diagnostics for one session; logged periodically during a transfer so the logs
        // show what actually limits throughput (negotiated send rate, wire rate, queued reliable bytes).
        // Reflected, not compiled: the status struct was renamed between the Steamworks.NET version this
        // project compiles against (SteamNetworkingQuickConnectionStatus, pkg 20.0.0) and the one the game
        // ships (SteamNetConnectionRealTimeStatus_t); same fields, so we bind at runtime by name.
        private static System.Reflection.MethodInfo s_statsMethod;
        private static bool s_statsMethodMissing;
        private static System.Reflection.FieldInfo s_fPing, s_fRate, s_fWire, s_fPend;
        private static System.Reflection.PropertyInfo s_pDesc;

        internal static bool TryGetSessionNumbers(ulong peerSteamId, out int pingMs, out int sendRateBps,
            out float wireBps, out long pendingReliable, out string transport)
        {
            pingMs = 0; sendRateBps = 0; wireBps = 0f; pendingReliable = 0; transport = "?";
            try
            {
                if (s_statsMethodMissing)
                    return false;
                if (s_statsMethod == null)
                {
                    s_statsMethod = typeof(SteamNetworkingMessages).GetMethod("GetSessionConnectionInfo");
                    if (s_statsMethod == null) { s_statsMethodMissing = true; return false; }
                }
                var id = default(SteamNetworkingIdentity);
                id.SetSteamID64(peerSteamId);
                var args = new object[] { id, null, null };
                s_statsMethod.Invoke(null, args);
                object info = args[1], rt = args[2];
                if (rt == null)
                    return false;
                var t = rt.GetType();
                if (s_fPing == null)
                {
                    s_fPing = t.GetField("m_nPing");
                    s_fRate = t.GetField("m_nSendRateBytesPerSecond");
                    s_fWire = t.GetField("m_flOutBytesPerSec");
                    s_fPend = t.GetField("m_cbPendingReliable");
                }
                if (s_fPing?.GetValue(rt) is int p) pingMs = p;
                if (s_fRate?.GetValue(rt) is int r) sendRateBps = r;
                if (s_fWire?.GetValue(rt) is float w) wireBps = w;
                if (s_fPend?.GetValue(rt) is int pe) pendingReliable = pe;
                if (info != null)
                {
                    if (s_pDesc == null)
                        s_pDesc = info.GetType().GetProperty("m_szConnectionDescription");
                    transport = (s_pDesc?.GetValue(info) as string) ?? "?";
                }
                return true;
            }
            catch (Exception)
            {
                s_statsMethodMissing = true;
                return false;
            }
        }

        internal static string SessionStats(ulong peerSteamId)
        {
            if (!TryGetSessionNumbers(peerSteamId, out int ping, out int rate, out float wire, out long pend, out string via))
                return "stats unavailable";
            return string.Format("ping={0}ms sendRate={1}KB/s wire={2}KB/s pendingReliable={3}KB via [{4}]",
                ping, rate / 1024, (int)(wire / 1024f), pend / 1024, via);
        }

        // The live rate knob (see EnsureInit). Idempotent per value; changes are logged by SetGlobalConfigInt.
        private static int s_lastRateFloor;
        internal static void SetSendRateFloor(int bytesPerSecond)
        {
            if (bytesPerSecond == s_lastRateFloor)
                return;
            if (SetGlobalConfigInt(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, bytesPerSecond))
            {
                s_lastRateFloor = bytesPerSecond;
            }
        }

        internal static void CloseSession(ulong peerSteamId)
        {
            try
            {
                var id = default(SteamNetworkingIdentity);
                id.SetSteamID64(peerSteamId);
                SteamNetworkingMessages.CloseSessionWithUser(ref id);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow("[SteamP2P] CloseSession: " + e.Message);
            }
        }

        // Drains all queued messages on our channel. Called each frame by the pump (main thread).
        internal static void Pump()
        {
            var handler = MessageHandler;
            if (handler == null || !s_available)
                return;
            int n;
            do
            {
                try
                {
                    n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, s_recvBuf, s_recvBuf.Length);
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow("[SteamP2P][ERR] receive pump: " + e.Message);
                    return;
                }
                for (int i = 0; i < n; i++)
                {
                    IntPtr ptr = s_recvBuf[i];
                    try
                    {
                        var msg = SteamNetworkingMessage_t.FromIntPtr(ptr);
                        ulong from = msg.m_identityPeer.GetSteamID64();
                        var data = new byte[msg.m_cbSize];
                        Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                        handler(from, data);
                    }
                    catch (Exception e)
                    {
                        MultiplayerStabilityMain.LogNoThrow("[SteamP2P][ERR] pump: " + e);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(ptr);   // static Release(IntPtr) ALWAYS
                    }
                }
            }
            while (n == s_recvBuf.Length);
        }

        private static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t req)
        {
            try
            {
                ulong who = req.m_identityRemote.GetSteamID64();
                if (s_acceptFrom.Contains(who))
                {
                    SteamNetworkingMessages.AcceptSessionWithUser(ref req.m_identityRemote);
                    MultiplayerStabilityMain.LogNoThrow("[SteamP2P] Accepted P2P session from " + who);
                }
                else
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[SteamP2P] Ignored unexpected P2P session from " + who);
                }
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[SteamP2P][ERR] session-request handler: " + e.Message);
            }
        }

        private static void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t f)
        {
            ulong who = f.m_info.m_identityRemote.GetSteamID64();
            MultiplayerStabilityMain.LogNoThrow("[SteamP2P] Session failed with " + who + " (state=" + f.m_info.m_eState + ")");
            try
            {
                SessionFailed?.Invoke(who);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow("[SteamP2P][ERR] session-failure handler: " + e.Message);
            }
        }
    }

    // Drives the receive pump every frame. Created and kept alive by SteamP2P.EnsureInit.
    internal sealed class SteamP2PPump : MonoBehaviour
    {
        private void Update() => SteamP2P.Pump();
    }
}
