using System;
using BepInEx.Logging;
using Steamworks;

namespace AdaptiveNet
{
    /// <summary>
    /// Routes every Steamworks networking call to the interface that owns the connection handle
    /// in this process.
    /// </summary>
    /// <remarks>
    /// Valheim ships two builds of assembly_valheim.dll and ZSteamSocket is not the same code in
    /// both (verified with Mono.Cecil on 2026-08-24):
    ///
    ///   valheim_Data\Managed        (client)    -> SteamNetworkingSockets / SteamNetworkingUtils
    ///   valheim_server_Data\Managed (dedicated) -> SteamGameServerNetworkingSockets / ...Utils
    ///
    /// A connection handle only exists inside the interface that created it, so there is no
    /// single correct namespace to compile in — which is why this choice was flipped twice
    /// before, once in each direction, each time "fixing" one process by breaking the other.
    /// Steamworks.NET also makes the wrong choice expensive: every call opens with
    /// InteropHelp.TestIfAvailableClient or TestIfAvailableGameServer, which <em>throw</em>
    /// instead of returning an error code, so a mismatch raises rather than degrades.
    ///
    /// The mode is therefore probed once at runtime and cached. Racing callers compute the same
    /// answer and write the same value to a field whose assignment is atomic, so no lock.
    /// </remarks>
    internal static class SteamInterface
    {
        internal enum Mode
        {
            Unresolved,
            User,
            GameServer,
            Unavailable,
        }

        private static ManualLogSource _log;
        private static Mode _mode;

        public static void Initialize(ManualLogSource log)
        {
            _log = log;
            _mode = Mode.Unresolved;
        }

        public static void Reset()
        {
            _mode = Mode.Unresolved;
            _log = null;
        }

        public static Mode Current
        {
            get
            {
                Mode mode = _mode;
                if (mode == Mode.Unresolved)
                {
                    mode = Probe();
                    _mode = mode;
                }

                return mode;
            }
        }

        public static bool Available => Current != Mode.Unavailable;

        public static EResult GetConnectionRealTimeStatus(
            HSteamNetConnection connection,
            ref SteamNetConnectionRealTimeStatus_t status,
            int lanes,
            ref SteamNetConnectionRealTimeLaneStatus_t laneStatus)
        {
            switch (Current)
            {
                case Mode.GameServer:
                    return SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(
                        connection, ref status, lanes, ref laneStatus);
                case Mode.User:
                    return SteamNetworkingSockets.GetConnectionRealTimeStatus(
                        connection, ref status, lanes, ref laneStatus);
                default:
                    return EResult.k_EResultServiceUnavailable;
            }
        }

        public static bool SetConfigValue(
            ESteamNetworkingConfigValue key,
            ESteamNetworkingConfigScope scope,
            IntPtr scopeObject,
            ESteamNetworkingConfigDataType dataType,
            IntPtr value)
        {
            switch (Current)
            {
                case Mode.GameServer:
                    return SteamGameServerNetworkingUtils.SetConfigValue(
                        key, scope, scopeObject, dataType, value);
                case Mode.User:
                    return SteamNetworkingUtils.SetConfigValue(
                        key, scope, scopeObject, dataType, value);
                default:
                    return false;
            }
        }

        public static EResult SendMessageToConnection(
            HSteamNetConnection connection,
            IntPtr data,
            uint length,
            int sendFlags,
            out long messageNumber)
        {
            switch (Current)
            {
                case Mode.GameServer:
                    return SteamGameServerNetworkingSockets.SendMessageToConnection(
                        connection, data, length, sendFlags, out messageNumber);
                case Mode.User:
                    return SteamNetworkingSockets.SendMessageToConnection(
                        connection, data, length, sendFlags, out messageNumber);
                default:
                    messageNumber = 0L;
                    return EResult.k_EResultServiceUnavailable;
            }
        }

        private static Mode Probe()
        {
            bool gameServerFirst = PrefersGameServer();
            Mode first = gameServerFirst ? Mode.GameServer : Mode.User;
            Mode second = gameServerFirst ? Mode.User : Mode.GameServer;

            if (CanQuery(first)) return Announce(first);
            if (CanQuery(second)) return Announce(second);

            _log?.LogWarning(
                "Neither Steamworks networking interface answered in this process. AdaptiveNet keeps " +
                "sampling through Valheim's own ISocket metrics and leaves per-connection limits alone.");
            return Mode.Unavailable;
        }

        /// <summary>
        /// Asks an interface whether it is usable, without needing a live connection.
        /// </summary>
        /// <remarks>
        /// Steamworks.NET runs its availability test before the native call, so an unavailable
        /// interface throws while an available one merely reports that the handle names no
        /// connection. Probing with <see cref="HSteamNetConnection.Invalid"/> therefore separates
        /// "wrong interface" from "stale handle" — a distinction a probe against a real
        /// connection could not make, because both fail there.
        /// </remarks>
        private static bool CanQuery(Mode mode)
        {
            var status = default(SteamNetConnectionRealTimeStatus_t);
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            try
            {
                if (mode == Mode.GameServer)
                {
                    SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(
                        HSteamNetConnection.Invalid, ref status, 0, ref lanes);
                }
                else
                {
                    SteamNetworkingSockets.GetConnectionRealTimeStatus(
                        HSteamNetConnection.Invalid, ref status, 0, ref lanes);
                }

                return true;
            }
            catch (Exception exception)
            {
                _log?.LogDebug(
                    $"Steamworks {mode} interface is not available here: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        /// <summary>
        /// Orders the probe; it does not decide it. A dedicated server loads the build that uses
        /// the game-server interface, while a listen server loads the client build even though it
        /// is also serving — so this is the right question to ask and the wrong answer to trust
        /// on its own.
        /// </summary>
        private static bool PrefersGameServer()
        {
            try
            {
                return ZNet.instance != null && ZNet.instance.IsDedicated();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Mode Announce(Mode mode)
        {
            _log?.LogInfo(
                mode == Mode.GameServer
                    ? "Steam networking resolved to the game-server interface (dedicated-server build)."
                    : "Steam networking resolved to the user interface (client build).");
            return mode;
        }
    }
}
