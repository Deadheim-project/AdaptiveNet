#nullable enable
using System;

namespace AdaptiveNet.Core
{
    /// <summary>
    /// The vanilla per-connection metrics AdaptiveNet reads, restated without a reference to
    /// Valheim's ISocket. The indirection exists so the fallback arithmetic below can be
    /// exercised in a plain test process: ISocket drags in assembly_valheim, and the
    /// transport-specific sampler above it drags in Steamworks, neither of which can be
    /// loaded outside the game.
    /// </summary>
    public interface IVanillaConnectionMetrics
    {
        void GetConnectionQuality(
            out float localQuality,
            out float remoteQuality,
            out int pingMilliseconds,
            out float outgoingBytesPerSecond,
            out float incomingBytesPerSecond);

        int GetSendQueueSize();

        int GetCurrentSendRate();
    }

    /// <summary>
    /// Produces the richer transport-native sample for links that have one. Returns false when
    /// this transport has nothing better to offer than the vanilla metrics, and is allowed to
    /// throw: an unavailable Steamworks interface raises instead of returning an error code.
    /// </summary>
    public delegate bool TransportSampler(out NetworkSample sample);

    /// <summary>
    /// Chooses between the transport-native sample and the vanilla ISocket metrics.
    /// </summary>
    public static class ConnectionSampler
    {
        /// <param name="transportSampler">
        /// The transport-native sampler, or null for links that have none.
        /// </param>
        /// <param name="onFailure">
        /// Receives the sampling stage that failed and its exception. Called at most once per
        /// stage per call.
        /// </param>
        public static bool TrySample(
            IVanillaConnectionMetrics metrics,
            TransportSampler? transportSampler,
            Action<string, Exception>? onFailure,
            out NetworkSample sample)
        {
            try
            {
                if (transportSampler != null &&
                    TryTransportSample(transportSampler, onFailure, out sample))
                {
                    return true;
                }

                metrics.GetConnectionQuality(
                    out float localQuality,
                    out float remoteQuality,
                    out int ping,
                    out float outgoingBytesPerSecond,
                    out float incomingBytesPerSecond);
                int queueBytes = Math.Max(0, metrics.GetSendQueueSize());
                int currentSendRate = 0;
                try
                {
                    currentSendRate = Math.Max(0, metrics.GetCurrentSendRate());
                }
                catch (NotImplementedException)
                {
                    // PlayFab currently leaves this optional metric unimplemented.
                }
                double queueDelayDenominator = outgoingBytesPerSecond > 1f
                    ? outgoingBytesPerSecond
                    : currentSendRate;
                double estimatedQueueDelay = queueDelayDenominator > 1d
                    ? queueBytes * 1000d / queueDelayDenominator
                    : queueBytes > 0 ? 1000d : 0d;
                sample = new NetworkSample(
                    true,
                    ping,
                    localQuality,
                    remoteQuality,
                    queueBytes,
                    0,
                    0,
                    0,
                    estimatedQueueDelay,
                    currentSendRate,
                    outgoingBytesPerSecond,
                    incomingBytesPerSecond,
                    0d,
                    0d);
                return true;
            }
            catch (Exception exception)
            {
                onFailure?.Invoke("Connection sample", exception);
                sample = default;
                return false;
            }
        }

        /// <summary>
        /// Keeps a transport failure from aborting the vanilla fallback beside it. An interface
        /// that is unavailable in this process throws instead of returning an error code, so
        /// without this boundary a single throw costs every metric rather than only the
        /// transport-specific ones.
        /// </summary>
        private static bool TryTransportSample(
            TransportSampler transportSampler,
            Action<string, Exception>? onFailure,
            out NetworkSample sample)
        {
            try
            {
                return transportSampler(out sample);
            }
            catch (Exception exception)
            {
                onFailure?.Invoke("Steam transport sample", exception);
                sample = default;
                return false;
            }
        }
    }
}
