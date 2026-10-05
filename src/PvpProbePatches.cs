using System;
using System.Reflection;
using HarmonyLib;

namespace AdaptiveNet
{
    // The PvP probes only observe. Each one resolves its target itself and declines in Prepare when
    // the game no longer has it, so a Valheim update costs that probe rather than failing PatchAll,
    // which would take every other AdaptiveNet patch down with it.

    [HarmonyPatch]
    internal static class ZdoArrivalProbePatch
    {
        private static MethodBase Target() =>
            AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });

        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();
        private static void Postfix(ZRpc __0) => PvpTelemetry.OnZdoDataReceived(__0);
    }

    [HarmonyPatch]
    internal static class ZdoServiceProbePatch
    {
        private static MethodBase Target()
        {
            Type peerType = typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic);
            return peerType == null
                ? null
                : AccessTools.Method(typeof(ZDOMan), "SendZDOs", new[] { peerType, typeof(bool) });
        }

        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();
        private static void Postfix(object __0, bool __1, bool __result) => PvpTelemetry.OnSendZdos(__0, __1, __result);
    }

    [HarmonyPatch]
    internal static class HitRouteProbePatch
    {
        private static MethodBase Target() =>
            AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) });

        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();
        private static void Prefix(ZRoutedRpc.RoutedRPCData __0) => PvpTelemetry.OnRouteRpc(__0);
    }

    [HarmonyPatch]
    internal static class WorldSaveProbePatch
    {
        private static MethodBase Target() => AccessTools.Method(typeof(ZNet), "SaveWorld", new[] { typeof(bool) });

        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();
        private static void Prefix() => PvpTelemetry.OnWorldSaveStarting();
        private static void Postfix(bool __0) => PvpTelemetry.OnWorldSaveFinished(__0);
    }
}
