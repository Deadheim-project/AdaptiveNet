using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Steamworks;

namespace AdaptiveNet
{
    internal static class PinnedSteamSender
    {
        private static ManualLogSource _log;
        private static bool _faulted;
        private static bool _limitExceededLogged;

        public static void Initialize(ManualLogSource log)
        {
            _log = log;
            _faulted = false;
            _limitExceededLogged = false;
        }

        public static void Reset()
        {
            _faulted = false;
            _limitExceededLogged = false;
            _log = null;
        }

        /// <summary>
        /// Returns true when the vanilla implementation should be skipped.
        /// </summary>
        public static unsafe bool TryHandle(ZSteamSocket socket)
        {
            if (_faulted || socket == null)
            {
                return false;
            }

            try
            {
                if (!socket.IsConnected())
                {
                    return true;
                }

                Queue<byte[]> sendQueue = GameAccess.GetSendQueue(socket);
                HSteamNetConnection connection = GameAccess.GetConnection(socket);
                while (sendQueue.Count > 0)
                {
                    byte[] packet = sendQueue.Peek();
                    if (packet == null)
                    {
                        sendQueue.Dequeue();
                        continue;
                    }

                    EResult result;
                    long messageNumber;
                    fixed (byte* packetPointer = packet)
                    {
                        // Dedicated-server only (PinnedSteamSendPatch gates on IsDedicated),
                        // but routed through SteamInterface anyway so there is exactly one
                        // place in the mod that decides which Steamworks half to call.
                        result = SteamInterface.SendMessageToConnection(
                            connection,
                            new IntPtr(packetPointer),
                            (uint)packet.Length,
                            8,
                            out messageNumber);
                    }

                    if (result != EResult.k_EResultOK)
                    {
                        if (IsRetryLater(result))
                        {
                            // Vanilla's own answer to these: stop, keep the packet queued and
                            // let the next Update try again.
                            if (result == EResult.k_EResultLimitExceeded && !_limitExceededLogged)
                            {
                                _limitExceededLogged = true;
                                _log?.LogInfo(
                                    "Steam send buffer full on a connection; its packets stay queued and are retried next frame, as in Valheim. Logged once.");
                            }
                            return true;
                        }

                        _faulted = true;
                        _log?.LogWarning(
                            $"Pinned Steam send returned {result}; disabling the pinned path and reverting to Valheim for the remaining queue.");
                        return false;
                    }

                    sendQueue.Dequeue();
                    GameAccess.AddTotalSent(socket, packet.Length);
                }

                return true;
            }
            catch (Exception exception)
            {
                _faulted = true;
                _log?.LogError(
                    $"Pinned Steam send failed; reverting to Valheim copy path: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        /// <summary>
        /// Results a healthy server meets routinely, which must not cost the pinned path for the
        /// rest of the process (each used to, the first time any player hit one):
        /// LimitExceeded is backpressure from a full SendBufferSize; NoConnection is a peer
        /// that closed or timed out (ClosedByPeer / ProblemDetectedLocally) before Valheim ran
        /// the status callback that closes its socket; InvalidState is the same connection one
        /// step later. Anything else still means the pinned path itself is wrong.
        /// </summary>
        internal static bool IsRetryLater(EResult result)
        {
            return result == EResult.k_EResultLimitExceeded ||
                   result == EResult.k_EResultNoConnection ||
                   result == EResult.k_EResultInvalidState;
        }
    }
}
