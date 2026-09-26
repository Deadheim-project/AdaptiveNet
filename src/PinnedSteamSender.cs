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

        public static void Initialize(ManualLogSource log)
        {
            _log = log;
            _faulted = false;
        }

        public static void Reset()
        {
            _faulted = false;
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
    }
}
