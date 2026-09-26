using System;

namespace AdaptiveNet.Core
{
    public sealed class PeerSchedulerOptions
    {
        public int GroupRadiusMeters { get; set; } = 160;
        public int GroupMinimumPlayers { get; set; } = 2;
        public int GroupedPeerIntervalMilliseconds { get; set; } = 100;
        public int SoloPeerIntervalMilliseconds { get; set; } = 250;
        public int ClientToServerIntervalMilliseconds { get; set; } = 50;
        public int MaximumPeersPerFrame { get; set; } = 12;
        public double CpuBudgetMilliseconds { get; set; } = 0.75d;

        public void Validate()
        {
            GroupRadiusMeters = Math.Max(20, Math.Min(1000, GroupRadiusMeters));
            GroupMinimumPlayers = Math.Max(2, Math.Min(20, GroupMinimumPlayers));
            GroupedPeerIntervalMilliseconds = Math.Max(25, Math.Min(1000, GroupedPeerIntervalMilliseconds));
            SoloPeerIntervalMilliseconds = Math.Max(GroupedPeerIntervalMilliseconds, Math.Min(2000, SoloPeerIntervalMilliseconds));
            ClientToServerIntervalMilliseconds = Math.Max(25, Math.Min(1000, ClientToServerIntervalMilliseconds));
            MaximumPeersPerFrame = Math.Max(1, Math.Min(64, MaximumPeersPerFrame));
            CpuBudgetMilliseconds = Math.Max(0.1d, Math.Min(20d, CpuBudgetMilliseconds));
        }
    }

    public static class PeerCadencePolicy
    {
        public static int GetIntervalMilliseconds(
            bool isServer,
            int nearbyPlayerCount,
            int connectedPlayerCount,
            PeerSchedulerOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (!isServer)
            {
                return options.ClientToServerIntervalMilliseconds;
            }

            if (connectedPlayerCount <= 4 ||
                nearbyPlayerCount >= options.GroupMinimumPlayers && nearbyPlayerCount <= 4)
            {
                return Math.Min(50, options.GroupedPeerIntervalMilliseconds);
            }

            if (nearbyPlayerCount >= options.GroupMinimumPlayers && nearbyPlayerCount <= 8)
            {
                return Math.Min(75, options.GroupedPeerIntervalMilliseconds);
            }

            return nearbyPlayerCount >= options.GroupMinimumPlayers
                ? options.GroupedPeerIntervalMilliseconds
                : options.SoloPeerIntervalMilliseconds;
        }
    }
}
