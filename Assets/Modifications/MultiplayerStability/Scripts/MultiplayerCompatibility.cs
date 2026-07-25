// Session-latched compatibility gate for simulation-changing patches and custom wire protocols.
//
// Peers exchange their compiled module IDs while assembling the lobby. At each save-transfer epoch,
// the uploading peer evaluates those reports and the complete Photon mod list, then sends one framed,
// reliable decision directly to every other peer before vanilla sends LoadSave. Downloading peers apply
// the decision from the actual save sender at DownloadSave. This preserves vanilla's rule that any peer
// may start a save and that the lower actor number wins simultaneous starts.
//
// Mixed or unresolved sender data selects vanilla behavior. A failed decision send, or a missing or
// invalid decision when another peer advertises the mod, aborts the load so peers cannot enter play
// with different policies.
//
// This protects modded peers that contain this gate and protocol. Pre-0.9 builds do not honor the
// decision frame and remain unsupported in mixed-version sessions.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Kingmaker.Networking;
using Kingmaker.Networking.NetGameFsm;
using Photon.Realtime;

namespace MultiplayerStability
{
    internal static class MultiplayerCompatibility
    {
        internal enum LatchState
        {
            Unknown,
            Solo,
            Compatible,
            Incompatible
        }

        private enum Evaluation
        {
            Unresolved,
            Solo,
            Compatible,
            Incompatible
        }

        private sealed class PendingDecision
        {
            internal int SenderActor;
            internal bool Enabled;
            internal string Version;
            internal Guid ModuleId;
            internal int PlayerCount;
            internal uint RosterHash;
        }

        private const byte ControlCode = 100;
        private const uint DecisionMagic = 0x4353504D; // "MPSC" in little-endian byte order
        private const uint HelloMagic = 0x4853504D; // "MPSH" in little-endian byte order
        private const byte DecisionProtocolVersion = 1;
        private const int DecisionFixedSize = 32;
        private const int HelloFixedSize = 23;
        private const int MaxVersionBytes = 128;

        private static readonly Guid LocalModuleId =
            Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId;
        private static readonly Dictionary<int, BuildIdentity> s_peerBuilds =
            new Dictionary<int, BuildIdentity>();
        private static readonly HashSet<int> s_helloRecipients =
            new HashSet<int>();
        private static readonly Dictionary<int, PendingDecision> s_pendingDecisions =
            new Dictionary<int, PendingDecision>();

        private static LatchState s_state;
        private static string s_detail = "not evaluated";

        private sealed class BuildIdentity
        {
            internal string Version;
            internal Guid ModuleId;
        }

        internal static LatchState State => s_state;
        internal static string Detail => s_detail;
        internal static bool SimulationFixesEnabled => s_state == LatchState.Compatible;
        internal static bool ProtocolsEnabled => s_state == LatchState.Compatible;

        internal static void Reset(string reason)
        {
            s_pendingDecisions.Clear();
            s_peerBuilds.Clear();
            s_helloRecipients.Clear();
            s_state = LatchState.Unknown;
            s_detail = reason;
            MultiplayerStabilityMain.LogNoThrow("[Compat] Reset (" + reason + ").");
        }

        internal static void TrySendBuildHello()
        {
            try
            {
                var photon = PhotonManager.Instance;
                if (photon == null || !photon.InRoom)
                    return;

                string version = MultiplayerStabilityMain.Modification?.Manifest?.Version;
                byte[] versionBytes = Encoding.UTF8.GetBytes(version ?? string.Empty);
                if (versionBytes.Length > MaxVersionBytes)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Compat][ERR] manifest version is too long for the build hello.");
                    return;
                }

                byte[] frame = new byte[HelloFixedSize + versionBytes.Length];
                WriteUInt32(frame, 0, HelloMagic);
                frame[4] = DecisionProtocolVersion;
                Buffer.BlockCopy(LocalModuleId.ToByteArray(), 0, frame, 5, 16);
                WriteUInt16(frame, 21, (ushort)versionBytes.Length);
                Buffer.BlockCopy(versionBytes, 0, frame, HelloFixedSize, versionBytes.Length);

                int localActor = photon.LocalClientId;
                var players = photon.AllPlayers;
                for (int i = 0; i < players.Count; i++)
                {
                    int actor = players[i].Player.ActorNumber;
                    if (actor == localActor || s_helloRecipients.Contains(actor))
                        continue;

                    if (photon.SendMessageTo(
                        new PhotonActorNumber(actor), ControlCode, frame, 0, frame.Length))
                    {
                        s_helloRecipients.Add(actor);
                        MultiplayerStabilityMain.LogNoThrow(
                            "[Compat] Build identity sent to actor " + actor + ".");
                    }
                }
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Compat][ERR] build identity send failed: " + e.Message);
            }
        }

        internal static void TryLatchLobby()
        {
            if (!IsLobbyState())
                return;

            Evaluation evaluation = EvaluateRoster(
                out string detail, out _, out _, out _);
            switch (evaluation)
            {
                case Evaluation.Solo:
                    SetState(LatchState.Solo, "lobby: " + detail);
                    break;
                case Evaluation.Compatible:
                    SetState(LatchState.Compatible, "lobby: " + detail);
                    break;
                case Evaluation.Incompatible:
                    SetState(LatchState.Incompatible, "lobby: " + detail);
                    break;
                default:
                    SetState(LatchState.Unknown, "lobby: " + detail);
                    break;
            }
        }

        internal static bool BeginTransferEpoch()
        {
            Evaluation evaluation = EvaluateRoster(
                out string detail,
                out string localVersion,
                out int playerCount,
                out uint rosterHash);

            if (evaluation == Evaluation.Solo)
            {
                SetState(LatchState.Solo, "save upload epoch: " + detail);
                return true;
            }

            bool enabled = evaluation == Evaluation.Compatible;
            if (!SendDecision(enabled, localVersion ?? string.Empty, playerCount, rosterHash))
            {
                SetState(
                    LatchState.Incompatible,
                    "save upload epoch: compatibility decision could not be sent");
                return false;
            }

            SetState(
                enabled ? LatchState.Compatible : LatchState.Incompatible,
                "save upload epoch: sender decision distributed; " + detail);
            return true;
        }

        internal static bool ApplyTransferEpoch(PhotonActorNumber saveFromPlayer)
        {
            int saveSender = saveFromPlayer.ActorNumber;
            s_pendingDecisions.TryGetValue(saveSender, out PendingDecision decision);
            s_pendingDecisions.Remove(saveSender);

            if (decision == null)
            {
                if (!CanUseVanillaWithoutDecision(out string detail))
                {
                    SetState(
                        LatchState.Incompatible,
                        "save download epoch: " + detail);
                    return false;
                }

                SetState(
                    LatchState.Incompatible,
                    "save download epoch: sender does not advertise the mod; vanilla behavior");
                return true;
            }

            try
            {
                if (decision.SenderActor != saveFromPlayer.ActorNumber)
                {
                    SetState(
                        LatchState.Incompatible,
                        "save download epoch: decision sender " + decision.SenderActor
                        + " differs from save sender " + saveFromPlayer.ActorNumber);
                    return false;
                }

                if (!decision.Enabled)
                {
                    SetState(
                        LatchState.Incompatible,
                        "save download epoch: sender selected vanilla behavior");
                    return true;
                }

                string localVersion = MultiplayerStabilityMain.Modification?.Manifest?.Version;
                if (!string.Equals(decision.Version, localVersion, StringComparison.Ordinal))
                {
                    SetState(
                        LatchState.Incompatible,
                        "save download epoch: sender build " + decision.Version
                        + " differs from local build " + (localVersion ?? "<unknown>"));
                    return false;
                }
                if (decision.ModuleId != LocalModuleId)
                {
                    SetState(
                        LatchState.Incompatible,
                        "save download epoch: sender has a different compiled module");
                    return false;
                }

                ComputeRoster(out int playerCount, out uint rosterHash);
                if (decision.PlayerCount != playerCount || decision.RosterHash != rosterHash)
                {
                    SetState(
                        LatchState.Incompatible,
                        "save download epoch: sender roster does not match local roster");
                    return false;
                }

                SetState(
                    LatchState.Compatible,
                    "save download epoch: sender enabled exact-build behavior");
                return true;
            }
            catch (Exception e)
            {
                SetState(
                    LatchState.Incompatible,
                    "save download epoch: decision validation failed: " + e.Message);
                return false;
            }
        }

        private static bool CanUseVanillaWithoutDecision(out string detail)
        {
            try
            {
                var photon = PhotonManager.Instance;
                var players = photon.AllPlayers;
                int localActor = photon.LocalClientId;
                for (int i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    if (player.Player.ActorNumber == localActor)
                        continue;
                    if (!photon.ActorNumberToPhotonPlayer(player.Player, out Player photonPlayer)
                        || !photon.GetPlayerProperty(photonPlayer, "m", out ModData[] mods)
                        || mods == null)
                    {
                        detail = "no sender decision and a peer mod list is unresolved";
                        return false;
                    }

                    for (int j = 0; j < mods.Length; j++)
                    {
                        if (mods[j] != null
                            && mods[j].Id == MultiplayerStabilityMain.UniqueName)
                        {
                            detail = "a peer advertises Multiplayer Stability but no sender decision arrived";
                            return false;
                        }
                    }
                }

                detail = "no other peer advertises Multiplayer Stability";
                return true;
            }
            catch (Exception e)
            {
                detail = "no sender decision and fallback validation failed: " + e.Message;
                return false;
            }
        }

        private static bool IsLobbyState()
        {
            try
            {
                return PhotonManager.NetGame.CurrentState == NetGame.State.InLobby;
            }
            catch
            {
                return false;
            }
        }

        private static Evaluation EvaluateRoster(
            out string detail,
            out string localVersion,
            out int playerCount,
            out uint rosterHash)
        {
            localVersion = MultiplayerStabilityMain.Modification?.Manifest?.Version;
            playerCount = 0;
            rosterHash = 0;

            try
            {
                var photon = PhotonManager.Instance;
                if (photon == null)
                {
                    detail = "PhotonManager unavailable";
                    return Evaluation.Unresolved;
                }

                ComputeRoster(out playerCount, out rosterHash);
                if (playerCount < 2)
                {
                    detail = "one player";
                    return Evaluation.Solo;
                }

                if (string.IsNullOrEmpty(localVersion))
                {
                    detail = "local manifest version unavailable";
                    return Evaluation.Unresolved;
                }

                var players = photon.AllPlayers;
                for (int i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    int actorNumber = player.Player.ActorNumber;
                    if (!photon.ActorNumberToPhotonPlayer(player.Player, out Player photonPlayer)
                        || !photon.GetPlayerProperty(photonPlayer, "m", out ModData[] mods)
                        || mods == null)
                    {
                        detail = "advertised mod property unavailable for actor "
                            + actorNumber;
                        return Evaluation.Unresolved;
                    }

                    ModData installed = null;
                    for (int j = 0; j < mods.Length; j++)
                    {
                        if (mods[j] != null
                            && mods[j].Id == MultiplayerStabilityMain.UniqueName)
                        {
                            installed = mods[j];
                            break;
                        }
                    }

                    if (installed == null)
                    {
                        detail = "actor " + actorNumber
                            + " does not advertise the mod";
                        return Evaluation.Incompatible;
                    }

                    if (!string.Equals(installed.Version, localVersion, StringComparison.Ordinal))
                    {
                        detail = "actor " + actorNumber + " has "
                            + installed.Version + ", local build is " + localVersion;
                        return Evaluation.Incompatible;
                    }

                    if (actorNumber == photon.LocalClientId)
                        continue;
                    if (!s_peerBuilds.TryGetValue(actorNumber, out BuildIdentity build))
                    {
                        detail = "build identity unavailable for actor " + actorNumber;
                        return Evaluation.Unresolved;
                    }
                    if (!string.Equals(build.Version, localVersion, StringComparison.Ordinal)
                        || build.ModuleId != LocalModuleId)
                    {
                        detail = "actor " + actorNumber
                            + " advertises the version but has a different compiled module";
                        return Evaluation.Incompatible;
                    }
                }

                detail = "exact " + localVersion + " parity across " + playerCount + " peers";
                return Evaluation.Compatible;
            }
            catch (Exception e)
            {
                detail = "evaluation failed: " + e.Message;
                return Evaluation.Unresolved;
            }
        }

        private static void ComputeRoster(out int playerCount, out uint rosterHash)
        {
            var players = PhotonManager.Instance.AllPlayers;
            playerCount = players.Count;
            var actors = new int[playerCount];
            for (int i = 0; i < playerCount; i++)
                actors[i] = players[i].Player.ActorNumber;
            Array.Sort(actors);

            uint hash = 2166136261U;
            for (int i = 0; i < actors.Length; i++)
            {
                uint actor = unchecked((uint)actors[i]);
                for (int shift = 0; shift < 32; shift += 8)
                {
                    hash ^= (byte)(actor >> shift);
                    hash *= 16777619U;
                }
            }
            rosterHash = hash;
        }

        private static bool SendDecision(
            bool enabled, string version, int playerCount, uint rosterHash)
        {
            try
            {
                byte[] versionBytes = Encoding.UTF8.GetBytes(version ?? string.Empty);
                if (versionBytes.Length > MaxVersionBytes)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Compat][ERR] manifest version is too long for the decision frame.");
                    return false;
                }

                byte[] frame = new byte[DecisionFixedSize + versionBytes.Length];
                WriteUInt32(frame, 0, DecisionMagic);
                frame[4] = DecisionProtocolVersion;
                frame[5] = enabled ? (byte)1 : (byte)0;
                WriteUInt16(frame, 6, (ushort)versionBytes.Length);
                WriteUInt32(frame, 8, unchecked((uint)playerCount));
                WriteUInt32(frame, 12, rosterHash);
                Buffer.BlockCopy(LocalModuleId.ToByteArray(), 0, frame, 16, 16);
                Buffer.BlockCopy(versionBytes, 0, frame, DecisionFixedSize, versionBytes.Length);

                var photon = PhotonManager.Instance;
                int localActor = photon.LocalClientId;
                int sent = 0;
                var players = photon.AllPlayers;
                for (int i = 0; i < players.Count; i++)
                {
                    int actor = players[i].Player.ActorNumber;
                    if (actor == localActor)
                        continue;
                    if (!photon.SendMessageTo(
                        new PhotonActorNumber(actor), ControlCode, frame, 0, frame.Length))
                    {
                        MultiplayerStabilityMain.LogNoThrow(
                            "[Compat][ERR] decision send failed for actor " + actor + ".");
                        return false;
                    }
                    sent++;
                }

                if (sent != playerCount - 1)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Compat][ERR] decision target count changed during send (sent "
                        + sent + ", expected " + (playerCount - 1) + ").");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Compat][ERR] decision send failed: " + e.Message);
                return false;
            }
        }

        internal static bool HasCompatibilityMagic(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 4)
                return false;
            uint magic = ReadUInt32(bytes, 0);
            return magic == DecisionMagic || magic == HelloMagic;
        }

        internal static void ReceiveCompatibilityFrame(
            int actorNumber, ReadOnlySpan<byte> bytes)
        {
            uint magic = ReadUInt32(bytes, 0);
            if (magic == DecisionMagic)
                ReceiveDecision(actorNumber, bytes);
            else if (magic == HelloMagic)
                ReceiveBuildHello(actorNumber, bytes);
            else
                throw new ArgumentException("unknown compatibility frame");
        }

        private static void ReceiveBuildHello(int actorNumber, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < HelloFixedSize)
                throw new ArgumentException("build hello is too short");
            if (bytes[4] != DecisionProtocolVersion)
                throw new ArgumentException("unsupported build hello protocol " + bytes[4]);

            int versionLength = ReadUInt16(bytes, 21);
            if (versionLength > MaxVersionBytes
                || bytes.Length != HelloFixedSize + versionLength)
            {
                throw new ArgumentException("invalid build hello version length");
            }

            var photon = PhotonManager.Instance;
            if (!photon.ActorNumberToPhotonPlayer(
                new PhotonActorNumber(actorNumber), out _))
            {
                throw new ArgumentException("build hello sender is not in the room");
            }

            s_peerBuilds[actorNumber] = new BuildIdentity
            {
                ModuleId = new Guid(bytes.Slice(5, 16).ToArray()),
                Version = Encoding.UTF8.GetString(
                    bytes.Slice(HelloFixedSize, versionLength).ToArray())
            };
            MultiplayerStabilityMain.LogNoThrow(
                "[Compat] Build identity received from actor " + actorNumber + ".");
            TryLatchLobby();
        }

        internal static void RemovePeer(int actorNumber)
        {
            s_peerBuilds.Remove(actorNumber);
            s_helloRecipients.Remove(actorNumber);
            s_pendingDecisions.Remove(actorNumber);
        }

        private static void ReceiveDecision(int actorNumber, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < DecisionFixedSize)
                throw new ArgumentException("decision frame is too short");
            if (bytes[4] != DecisionProtocolVersion)
                throw new ArgumentException("unsupported decision protocol " + bytes[4]);
            if (bytes[5] > 1)
                throw new ArgumentException("invalid decision value " + bytes[5]);

            int versionLength = ReadUInt16(bytes, 6);
            if (versionLength > MaxVersionBytes
                || bytes.Length != DecisionFixedSize + versionLength)
            {
                throw new ArgumentException("invalid decision version length");
            }

            if (!PhotonManager.Instance.ActorNumberToPhotonPlayer(
                new PhotonActorNumber(actorNumber), out _))
            {
                throw new ArgumentException("decision sender is not in the room");
            }

            var decision = new PendingDecision
            {
                SenderActor = actorNumber,
                Enabled = bytes[5] == 1,
                ModuleId = new Guid(bytes.Slice(16, 16).ToArray()),
                Version = Encoding.UTF8.GetString(
                    bytes.Slice(DecisionFixedSize, versionLength).ToArray()),
                PlayerCount = unchecked((int)ReadUInt32(bytes, 8)),
                RosterHash = ReadUInt32(bytes, 12)
            };
            s_pendingDecisions[actorNumber] = decision;

            MultiplayerStabilityMain.LogNoThrow(
                "[Compat] Received save decision from actor " + actorNumber + " for "
                + decision.PlayerCount + " peers: "
                + (decision.Enabled ? "exact-build behavior" : "vanilla behavior") + ".");
        }

        private static void SetState(LatchState state, string detail)
        {
            bool changed = s_state != state
                || !string.Equals(s_detail, detail, StringComparison.Ordinal);
            s_state = state;
            s_detail = detail;
            if (!changed)
                return;

            string behavior = state == LatchState.Compatible
                ? "simulation fixes and custom protocols enabled"
                : "simulation fixes and custom protocols disabled; vanilla behavior";
            MultiplayerStabilityMain.LogNoThrow(
                "[Compat] " + state + ": " + detail + " (" + behavior + ").");
        }

        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static int ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
        {
            return bytes[offset] | (bytes[offset + 1] << 8);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }
    }

    [HarmonyPatch(typeof(MessageNetManager), nameof(MessageNetManager.OnMessage))]
    internal static class MessageNetManager_OnMessage_Compatibility_Patch
    {
        private static bool Prefix(byte code, int actorNumber, ReadOnlySpan<byte> bytes)
        {
            if (code != 100 || !MultiplayerCompatibility.HasCompatibilityMagic(bytes))
                return true;

            try
            {
                MultiplayerCompatibility.ReceiveCompatibilityFrame(actorNumber, bytes);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Compat][ERR] rejected decision frame: " + e.Message);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnJoinedLobby))]
    internal static class ModsNetManager_OnJoinedLobby_Compatibility_Patch
    {
        private static void Prefix() => MultiplayerCompatibility.Reset("joined lobby");
        private static void Postfix()
        {
            MultiplayerCompatibility.TryLatchLobby();
            MultiplayerCompatibility.TrySendBuildHello();
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnPlayerUpdate))]
    internal static class ModsNetManager_OnPlayerUpdate_Compatibility_Patch
    {
        private static void Postfix(Player photonPlayer)
        {
            MultiplayerCompatibility.TryLatchLobby();
            MultiplayerCompatibility.TrySendBuildHello();
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnPlayerLeftRoom))]
    internal static class ModsNetManager_OnPlayerLeftRoom_Compatibility_Patch
    {
        private static void Postfix(Player otherPlayer)
        {
            try
            {
                MultiplayerCompatibility.RemovePeer(otherPlayer.ActorNumber);
                if (PhotonManager.NetGame.CurrentState == NetGame.State.InLobby)
                {
                    MultiplayerCompatibility.Reset("lobby player left");
                    MultiplayerCompatibility.TryLatchLobby();
                }
                MultiplayerCompatibility.TrySendBuildHello();
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Compat][ERR] player-left reevaluation failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnLeave))]
    internal static class ModsNetManager_OnLeave_Compatibility_Patch
    {
        private static void Postfix()
            => MultiplayerCompatibility.Reset("room leave");
    }

    [HarmonyPatch(typeof(SaveNetManager), nameof(SaveNetManager.UploadSave))]
    internal static class SaveNetManager_UploadSave_Compatibility_Patch
    {
        // Run before TransferBooster so the selected transport policy belongs to this epoch.
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            SaveNetManager __instance,
            ref Task __result)
        {
            if (__instance.InProcess)
                return true;
            if (MultiplayerCompatibility.BeginTransferEpoch())
                return true;

            __result = Task.FromException(
                new SendMessageFailException(
                    "Multiplayer Stability could not distribute the compatibility decision."));
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveNetManager), nameof(SaveNetManager.DownloadSave))]
    internal static class SaveNetManager_DownloadSave_Compatibility_Patch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            SaveNetManager __instance,
            PhotonActorNumber saveFromPlayer,
            ref Task<SaveNetManager.SaveReceiveData> __result)
        {
            if (__instance.InProcess)
                return true;
            if (MultiplayerCompatibility.ApplyTransferEpoch(saveFromPlayer))
                return true;

            __result = Task.FromException<SaveNetManager.SaveReceiveData>(
                new SendMessageFailException(
                    "Multiplayer Stability could not validate the save-sender compatibility decision."));
            return false;
        }
    }
}
