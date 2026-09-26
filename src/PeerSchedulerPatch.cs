using HarmonyLib;

namespace AdaptiveNet
{
    [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
    internal static class PeerSchedulerPatch
    {
        private static bool Prefix(ZDOMan __instance)
        {
            if (!NetworkRuntime.IsActive || !NetworkRuntime.SchedulerEnabled)
            {
                return true;
            }

            return !AdaptivePeerScheduler.TryHandle(__instance);
        }
    }
}
