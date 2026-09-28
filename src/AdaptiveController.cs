using System;

namespace AdaptiveNet.Core
{
    public sealed class AdaptiveControllerOptions
    {
        public int MinimumSendRateBytesPerSecond { get; set; } = 64 * 1024;
        public int MaximumSendRateBytesPerSecond { get; set; } = 4 * 1024 * 1024;
        public int InitialSendRateBytesPerSecond { get; set; } = 512 * 1024;
        public int AdditiveIncreaseBytesPerSecond { get; set; } = 64 * 1024;
        public double MultiplicativeDecreaseFactor { get; set; } = 0.75;
        public int HealthySamplesBeforeIncrease { get; set; } = 3;
        public double TargetQueueDelayMilliseconds { get; set; } = 60d;
        public double CongestedQueueDelayMilliseconds { get; set; } = 180d;
        public double MinimumConnectionQuality { get; set; } = 0.88d;
        public int PingInflationThresholdMilliseconds { get; set; } = 45;
        public int MinimumZdoQueueBudgetBytes { get; set; } = 10 * 1024;
        public int MaximumZdoQueueBudgetBytes { get; set; } = 48 * 1024;

        public void Validate()
        {
            MinimumSendRateBytesPerSecond = Math.Max(16 * 1024, MinimumSendRateBytesPerSecond);
            MaximumSendRateBytesPerSecond = Math.Max(MinimumSendRateBytesPerSecond, MaximumSendRateBytesPerSecond);
            InitialSendRateBytesPerSecond = Clamp(
                InitialSendRateBytesPerSecond,
                MinimumSendRateBytesPerSecond,
                MaximumSendRateBytesPerSecond);
            AdditiveIncreaseBytesPerSecond = Math.Max(1024, AdditiveIncreaseBytesPerSecond);
            MultiplicativeDecreaseFactor = Clamp(MultiplicativeDecreaseFactor, 0.25d, 0.95d);
            HealthySamplesBeforeIncrease = Math.Max(1, HealthySamplesBeforeIncrease);
            TargetQueueDelayMilliseconds = Clamp(TargetQueueDelayMilliseconds, 5d, 500d);
            CongestedQueueDelayMilliseconds = Math.Max(
                TargetQueueDelayMilliseconds + 5d,
                CongestedQueueDelayMilliseconds);
            MinimumConnectionQuality = Clamp(MinimumConnectionQuality, 0.1d, 1d);
            PingInflationThresholdMilliseconds = Math.Max(5, PingInflationThresholdMilliseconds);
            MinimumZdoQueueBudgetBytes = Math.Max(10 * 1024, MinimumZdoQueueBudgetBytes);
            MaximumZdoQueueBudgetBytes = Math.Max(MinimumZdoQueueBudgetBytes, MaximumZdoQueueBudgetBytes);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    public readonly struct NetworkSample
    {
        public NetworkSample(
            bool valid,
            int pingMilliseconds,
            double localQuality,
            double remoteQuality,
            int pendingBytes,
            int unackedBytes,
            double queueDelayMilliseconds,
            int transportSendRateBytesPerSecond,
            double outgoingBytesPerSecond,
            double incomingBytesPerSecond)
            : this(
                valid,
                pingMilliseconds,
                localQuality,
                remoteQuality,
                0,
                pendingBytes,
                0,
                unackedBytes,
                queueDelayMilliseconds,
                transportSendRateBytesPerSecond,
                outgoingBytesPerSecond,
                incomingBytesPerSecond,
                0d,
                0d)
        {
        }

        public NetworkSample(
            bool valid,
            int pingMilliseconds,
            double localQuality,
            double remoteQuality,
            int managedQueueBytes,
            int pendingReliableBytes,
            int pendingUnreliableBytes,
            int unackedBytes,
            double queueDelayMilliseconds,
            int transportSendRateBytesPerSecond,
            double outgoingBytesPerSecond,
            double incomingBytesPerSecond,
            double outgoingPacketsPerSecond,
            double incomingPacketsPerSecond)
        {
            Valid = valid;
            PingMilliseconds = Math.Max(0, pingMilliseconds);
            LocalQuality = localQuality;
            RemoteQuality = remoteQuality;
            ManagedQueueBytes = Math.Max(0, managedQueueBytes);
            PendingReliableBytes = Math.Max(0, pendingReliableBytes);
            PendingUnreliableBytes = Math.Max(0, pendingUnreliableBytes);
            PendingBytes = (int)Math.Min(
                int.MaxValue,
                (long)ManagedQueueBytes + PendingReliableBytes + PendingUnreliableBytes);
            UnackedBytes = Math.Max(0, unackedBytes);
            QueueDelayMilliseconds = Math.Max(0d, queueDelayMilliseconds);
            TransportSendRateBytesPerSecond = Math.Max(0, transportSendRateBytesPerSecond);
            OutgoingBytesPerSecond = Math.Max(0d, outgoingBytesPerSecond);
            IncomingBytesPerSecond = Math.Max(0d, incomingBytesPerSecond);
            OutgoingPacketsPerSecond = Math.Max(0d, outgoingPacketsPerSecond);
            IncomingPacketsPerSecond = Math.Max(0d, incomingPacketsPerSecond);
        }

        public bool Valid { get; }
        public int PingMilliseconds { get; }
        public double LocalQuality { get; }
        public double RemoteQuality { get; }
        public int ManagedQueueBytes { get; }
        public int PendingReliableBytes { get; }
        public int PendingUnreliableBytes { get; }
        public int PendingBytes { get; }
        public int UnackedBytes { get; }
        public double QueueDelayMilliseconds { get; }
        public int TransportSendRateBytesPerSecond { get; }
        public double OutgoingBytesPerSecond { get; }
        public double IncomingBytesPerSecond { get; }
        public double OutgoingPacketsPerSecond { get; }
        public double IncomingPacketsPerSecond { get; }
        public int TotalQueuedBytes => PendingBytes + UnackedBytes;
    }

    public readonly struct AdaptiveDecision
    {
        public AdaptiveDecision(
            int sendRateLimitBytesPerSecond,
            int zdoQueueBudgetBytes,
            bool congested,
            bool changed,
            double baselinePingMilliseconds,
            string reason)
        {
            SendRateLimitBytesPerSecond = sendRateLimitBytesPerSecond;
            ZdoQueueBudgetBytes = zdoQueueBudgetBytes;
            Congested = congested;
            Changed = changed;
            BaselinePingMilliseconds = baselinePingMilliseconds;
            Reason = reason ?? string.Empty;
        }

        public int SendRateLimitBytesPerSecond { get; }
        public int ZdoQueueBudgetBytes { get; }
        public bool Congested { get; }
        public bool Changed { get; }
        public double BaselinePingMilliseconds { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// A conservative AIMD controller. It opens bandwidth gradually when real demand is
    /// healthy and cuts the ceiling quickly when queue delay, quality, or ping inflation
    /// indicates congestion. No game state or packet format is changed.
    /// </summary>
    public sealed class AdaptiveConnectionController
    {
        private AdaptiveControllerOptions _options;
        private int _targetRate;
        private int _healthyDemandSamples;
        private double _baselinePing = -1d;

        public AdaptiveConnectionController(AdaptiveControllerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _targetRate = _options.InitialSendRateBytesPerSecond;
        }

        /// <summary>
        /// Takes options reloaded from the cfg without forgetting what this connection has
        /// learned: the ceiling it converged to and the ping baseline carry over, and the ceiling
        /// is only pulled inside the new bounds. Starting over from the initial rate would make
        /// every player re-climb for a minute each time an admin saves the file.
        /// </summary>
        public void UpdateOptions(AdaptiveControllerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();
            _options = options;
            _targetRate = Math.Max(
                _options.MinimumSendRateBytesPerSecond,
                Math.Min(_options.MaximumSendRateBytesPerSecond, _targetRate));
        }

        public int TargetSendRateBytesPerSecond => _targetRate;
        public double BaselinePingMilliseconds => Math.Max(0d, _baselinePing);

        public AdaptiveDecision Observe(NetworkSample sample)
        {
            if (!sample.Valid)
            {
                return CreateDecision(false, false, "sample-unavailable");
            }

            UpdateBaselinePing(sample);

            bool hasDemand = sample.TotalQueuedBytes >= 4 * 1024 ||
                             sample.OutgoingBytesPerSecond >= _targetRate * 0.35d;
            double connectionQuality = GetConnectionQuality(sample);
            bool qualityKnown = connectionQuality > 0d && connectionQuality <= 1d;
            bool qualityCongested = qualityKnown && connectionQuality < _options.MinimumConnectionQuality;
            bool queueCongested = sample.QueueDelayMilliseconds >= _options.CongestedQueueDelayMilliseconds;
            bool pingCongested = hasDemand &&
                                 _baselinePing > 0d &&
                                 sample.PingMilliseconds >= _baselinePing + _options.PingInflationThresholdMilliseconds;
            bool congested = qualityCongested || queueCongested || pingCongested;
            bool changed = false;
            string reason = "steady";

            if (congested)
            {
                // Never cut below what the link is already carrying. Observed 2026-08-24: a
                // quality-only dip drove the ceiling from 2624 to 64 KiB/s over ~13 consecutive
                // decreases, and the send queue delay went from 0 ms to 86 ms *because* of the
                // cut. A limiter that becomes the bottleneck is worse than no limiter, so the
                // floor tracks measured throughput. Genuine overload still shows up as queue
                // delay or ping inflation, which the transport's own control loop reacts to.
                int demandFloor = (int)Math.Min(
                    _options.MaximumSendRateBytesPerSecond,
                    Math.Max(0d, sample.OutgoingBytesPerSecond * 1.25d));
                int floor = Math.Max(_options.MinimumSendRateBytesPerSecond, demandFloor);
                int reduced = Math.Max(
                    floor,
                    (int)Math.Floor(_targetRate * _options.MultiplicativeDecreaseFactor));
                changed = reduced != _targetRate;
                _targetRate = reduced;
                _healthyDemandSamples = 0;
                reason = qualityCongested
                    ? "quality"
                    : queueCongested
                        ? "queue-delay"
                        : "ping-inflation";
            }
            else if (hasDemand && IsHealthyForGrowth(sample, qualityKnown))
            {
                _healthyDemandSamples++;
                if (_healthyDemandSamples >= _options.HealthySamplesBeforeIncrease)
                {
                    int increased = Math.Min(
                        _options.MaximumSendRateBytesPerSecond,
                        _targetRate + _options.AdditiveIncreaseBytesPerSecond);
                    changed = increased != _targetRate;
                    _targetRate = increased;
                    _healthyDemandSamples = 0;
                    reason = changed ? "healthy-growth" : "maximum";
                }
                else
                {
                    reason = "healthy-probe";
                }
            }
            else
            {
                _healthyDemandSamples = 0;
                reason = hasDemand ? "guarded" : "idle";
            }

            return CreateDecision(congested, changed, reason);
        }

        private bool IsHealthyForGrowth(NetworkSample sample, bool qualityKnown)
        {
            double growthQuality = Math.Min(0.98d, _options.MinimumConnectionQuality + 0.06d);
            bool qualityHealthy = !qualityKnown || GetConnectionQuality(sample) >= growthQuality;
            bool queueHealthy = sample.QueueDelayMilliseconds <= _options.TargetQueueDelayMilliseconds;
            bool pingHealthy = _baselinePing <= 0d ||
                               sample.PingMilliseconds <= _baselinePing + _options.PingInflationThresholdMilliseconds / 2d;
            return qualityHealthy && queueHealthy && pingHealthy;
        }

        private void UpdateBaselinePing(NetworkSample sample)
        {
            if (sample.PingMilliseconds <= 0)
            {
                return;
            }

            if (_baselinePing < 0d)
            {
                _baselinePing = sample.PingMilliseconds;
                return;
            }

            if (sample.PingMilliseconds < _baselinePing)
            {
                _baselinePing = _baselinePing * 0.8d + sample.PingMilliseconds * 0.2d;
                return;
            }

            bool queueCalm = sample.QueueDelayMilliseconds <= _options.TargetQueueDelayMilliseconds;
            double connectionQuality = GetConnectionQuality(sample);
            bool qualityCalm = connectionQuality <= 0d || connectionQuality >= _options.MinimumConnectionQuality;
            if (queueCalm && qualityCalm && sample.TotalQueuedBytes < 4 * 1024)
            {
                _baselinePing = _baselinePing * 0.98d + sample.PingMilliseconds * 0.02d;
            }
        }

        private static double GetConnectionQuality(NetworkSample sample)
        {
            bool localKnown = sample.LocalQuality > 0d && sample.LocalQuality <= 1d;
            bool remoteKnown = sample.RemoteQuality > 0d && sample.RemoteQuality <= 1d;
            if (localKnown && remoteKnown)
            {
                return Math.Min(sample.LocalQuality, sample.RemoteQuality);
            }
            return localKnown ? sample.LocalQuality : remoteKnown ? sample.RemoteQuality : 0d;
        }

        private AdaptiveDecision CreateDecision(bool congested, bool changed, string reason)
        {
            int queueBudget;
            if (congested)
            {
                queueBudget = _options.MinimumZdoQueueBudgetBytes;
            }
            else
            {
                double bytesInTargetWindow = _targetRate * (_options.TargetQueueDelayMilliseconds / 1000d);
                queueBudget = Clamp(
                    (int)Math.Round(bytesInTargetWindow),
                    _options.MinimumZdoQueueBudgetBytes,
                    _options.MaximumZdoQueueBudgetBytes);
            }

            return new AdaptiveDecision(
                _targetRate,
                queueBudget,
                congested,
                changed,
                BaselinePingMilliseconds,
                reason);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
