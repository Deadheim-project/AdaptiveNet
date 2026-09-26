using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;

namespace AdaptiveNet
{
    internal static class CompatibilityGuard
    {
        private static readonly Dictionary<string, string> KnownNetworkOverhauls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["VitByr.VBNetTweaks"] = "VBNetTweaks",
                ["CW_Jesse.BetterNetworking"] = "Better Networking",
                ["sighsorry.SkadiNet"] = "SkadiNet",
                ["CacoFFF.valheim.LeanNet"] = "LeanNet",
                ["Searica.Valheim.NetworkTweaks"] = "NetworkTweaks",
                ["org.bepinex.plugins.network"] = "Network",
                ["com.Fire.FiresGhettoNetworkMod"] = "Fires Ghetto Network Mod"
            };

        public static IReadOnlyList<string> Detect()
        {
            var found = new List<string>();
            foreach (KeyValuePair<string, string> candidate in KnownNetworkOverhauls)
            {
                if (Chainloader.PluginInfos.ContainsKey(candidate.Key))
                {
                    found.Add(candidate.Value);
                }
            }

            return found;
        }
    }
}
