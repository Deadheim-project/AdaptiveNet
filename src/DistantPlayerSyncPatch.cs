using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AdaptiveNet
{
    /// <summary>
    /// Vanilla CreateSyncList only adds the distant-sector ZDOs when fewer than ten near ones are
    /// pending, so a receiver standing in a busy base can go many seconds without an update of an
    /// enemy two or three zones away. This puts the other players' characters, within the
    /// configured radius, in front of that rule. The ZDO is still sent through the normal queue
    /// guard, budget and scheduler.
    /// </summary>
    [HarmonyPatch]
    internal static class DistantPlayerSyncPatch
    {
        private static MethodBase Target()
        {
            Type peerType = typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic);
            return peerType == null
                ? null
                : AccessTools.Method(typeof(ZDOMan), "CreateSyncList", new[] { peerType, typeof(List<ZDO>) });
        }

        private static bool Prepare() => Target() != null;
        private static MethodBase TargetMethod() => Target();

        private static void Postfix(object __0, List<ZDO> __1)
        {
            if (!NetworkRuntime.IsActive || !NetworkRuntime.DistantPlayerSyncEnabled) return;
            try
            {
                DistantPlayerSync.Add(__0, __1, NetworkRuntime.DistantPlayerSyncRadiusMeters);
            }
            catch (Exception exception)
            {
                DistantPlayerSync.Fault(exception);
            }
        }
    }

    internal static class DistantPlayerSync
    {
        private static bool _faulted;

        public static void Add(object zdoPeer, List<ZDO> toSync, int radiusMeters)
        {
            if (_faulted || zdoPeer == null || toSync == null || !GameAccess.PeerZdoRevisionAvailable) return;
            ZNet net = ZNet.instance;
            ZDOMan manager = ZDOMan.instance;
            if (net == null || manager == null || !net.IsServer()) return;

            ZNetPeer receiver = GameAccess.GetZNetPeer(zdoPeer);
            if (receiver == null) return;

            Vector3 origin = receiver.GetRefPos();
            float radiusSquared = (float)radiusMeters * radiusMeters;
            List<ZNetPeer> peers = net.GetPeers();
            for (int index = 0; index < peers.Count; index++)
            {
                ZNetPeer source = peers[index];
                if (source == null || ReferenceEquals(source, receiver)) continue;
                ZDOID character = source.m_characterID;
                if (character.IsNone()) continue;
                ZDO zdo = manager.GetZDO(character);
                if (zdo == null || (zdo.GetPosition() - origin).sqrMagnitude > radiusSquared) continue;
                if (!NeedsSend(zdoPeer, zdo) || toSync.Contains(zdo)) continue;
                toSync.Insert(0, zdo);
            }
        }

        /// <summary>The same test as ZDOPeer.ShouldSend, minus the owner revision (a player's character rarely changes owner).</summary>
        private static bool NeedsSend(object zdoPeer, ZDO zdo)
        {
            return !GameAccess.TryGetPeerZdoRevision(zdoPeer, zdo.m_uid, out uint sent) || zdo.DataRevision > sent;
        }

        public static void Fault(Exception exception)
        {
            _faulted = true;
            NetworkRuntime.ReportDistantPlayerSyncFault(exception);
        }
    }
}
