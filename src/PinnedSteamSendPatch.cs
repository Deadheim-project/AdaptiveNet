using HarmonyLib;

namespace AdaptiveNet
{
    [HarmonyPatch(typeof(ZSteamSocket), "SendQueuedPackages")]
    internal static class PinnedSteamSendPatch
    {
        private static bool Prefix(ZSteamSocket __instance)
        {
            bool dedicatedSteamServer = ZNet.instance != null &&
                                        ZNet.instance.IsDedicated() &&
                                        __instance != null;
            if (!NetworkRuntime.IsActive ||
                !NetworkRuntime.PinnedSteamSendEnabled ||
                !dedicatedSteamServer)
            {
                return true;
            }

            return !PinnedSteamSender.TryHandle(__instance);
        }
    }
}
