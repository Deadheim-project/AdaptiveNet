using System;
using AdaptiveNet.Core;
using Steamworks;

namespace AdaptiveNet
{
    internal static class SteamTransport
    {
        public static bool TrySample(ZSteamSocket socket, out NetworkSample sample)
        {
            sample = default;
            HSteamNetConnection connection = socket == null ? HSteamNetConnection.Invalid : GameAccess.GetConnection(socket);
            if (socket == null || connection == HSteamNetConnection.Invalid || !socket.IsConnected())
            {
                return false;
            }

            var status = default(SteamNetConnectionRealTimeStatus_t);
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            // Which Steamworks interface owns this handle depends on the process, because
            // Valheim's client and dedicated-server builds of ZSteamSocket create the
            // connection through different ones. SteamInterface resolves that once; see the
            // remarks there for the Cecil evidence.
            EResult result = SteamInterface.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes);
            if (result != EResult.k_EResultOK)
            {
                return false;
            }

            int managedQueueBytes = 0;
            foreach (byte[] packet in GameAccess.GetSendQueue(socket))
            {
                managedQueueBytes += packet?.Length ?? 0;
            }

            int pendingReliableBytes = Math.Max(0, status.m_cbPendingReliable);
            int pendingUnreliableBytes = Math.Max(0, status.m_cbPendingUnreliable);
            int unackedBytes = Math.Max(0, status.m_cbSentUnackedReliable);
            double queueDelayMilliseconds = Math.Max(
                0d,
                status.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1000d);

            sample = new NetworkSample(
                true,
                status.m_nPing,
                status.m_flConnectionQualityLocal,
                status.m_flConnectionQualityRemote,
                managedQueueBytes,
                pendingReliableBytes,
                pendingUnreliableBytes,
                unackedBytes,
                queueDelayMilliseconds,
                status.m_nSendRateBytesPerSecond,
                status.m_flOutBytesPerSec,
                status.m_flInBytesPerSec,
                status.m_flOutPacketsPerSec,
                status.m_flInPacketsPerSec);
            return true;
        }

        /// <remarks>
        /// Steam does not estimate bandwidth (Valve's SNP_ClampSendRate, verified 2026-10-03):
        /// each connection keeps one rate, set when it connects — Valheim's global
        /// SendRateMin = SendRateMax = 150 KiB/s — and every later change only clamps that
        /// rate into [minimum, maximum]. Raising the maximum therefore never speeds a
        /// connection up, while a maximum below the current rate lowers it for good, until the
        /// player reconnects. The minimum is what keeps that ratchet from going below vanilla.
        /// The real rate is SteamNetConnectionRealTimeStatus_t.m_nSendRateBytesPerSecond,
        /// sampled as NetworkSample.TransportSendRateBytesPerSecond.
        /// </remarks>
        public static bool ApplyConnectionLimits(
            ZSteamSocket socket,
            int minimumRateBytesPerSecond,
            int maximumRateBytesPerSecond,
            int sendBufferBytes,
            int nagleTimeMicroseconds)
        {
            HSteamNetConnection connection = socket == null ? HSteamNetConnection.Invalid : GameAccess.GetConnection(socket);
            if (socket == null || connection == HSteamNetConnection.Invalid)
            {
                return false;
            }

            bool ok = true;
            ok &= SetInt(connection,
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                minimumRateBytesPerSecond);
            ok &= SetInt(connection,
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                maximumRateBytesPerSecond);
            ok &= SetInt(connection,
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                sendBufferBytes);
            ok &= SetInt(connection,
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime,
                nagleTimeMicroseconds);
            return ok;
        }

        private static unsafe bool SetInt(
            HSteamNetConnection connection,
            ESteamNetworkingConfigValue key,
            int value)
        {
            int localValue = value;
            IntPtr scopeObject = new IntPtr(connection.m_HSteamNetConnection);
            IntPtr valuePointer = new IntPtr(&localValue);
            return SteamInterface.SetConfigValue(
                key,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                scopeObject,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                valuePointer);
        }
    }
}
