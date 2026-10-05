using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Steamworks;

namespace AdaptiveNet
{
    /// <summary>
    /// Validated, skip-visibility accessors for the few Valheim internals used by
    /// AdaptiveNet. Keeping these here avoids a runtime dependency on a publicizer.
    /// Construction fails before Harmony patches are installed if the game shape changed.
    /// </summary>
    internal static class GameAccess
    {
        private delegate bool PeerZdoRevisionGetter(object zdoPeer, ZDOID id, out uint dataRevision);

        private static Func<ZSteamSocket, HSteamNetConnection> _getConnection;
        private static Func<ZSteamSocket, Queue<byte[]>> _getSendQueue;
        private static Action<ZSteamSocket, int> _addTotalSent;
        private static Func<ZDOMan, IList> _getZdoPeers;
        private static Func<object, ZNetPeer> _getZNetPeer;
        private static Func<ZDOMan, object, bool, bool> _sendZdos;
        private static PeerZdoRevisionGetter _getPeerZdoRevision;

        public static void Initialize()
        {
            Type zdoPeerType = typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic);
            if (zdoPeerType == null)
            {
                throw new MissingMemberException(typeof(ZDOMan).FullName, "ZDOPeer");
            }

            _getConnection = CreateFieldGetter<ZSteamSocket, HSteamNetConnection>("m_con");
            _getSendQueue = CreateFieldGetter<ZSteamSocket, Queue<byte[]>>("m_sendQueue");
            _addTotalSent = CreateIntFieldAdder<ZSteamSocket>("m_totalSent");
            _getZdoPeers = CreateListGetter("m_peers");
            _getZNetPeer = CreateObjectFieldGetter<ZNetPeer>(zdoPeerType, "m_peer");
            _sendZdos = CreateSendZdos(zdoPeerType);

            // Diagnostic only: a game update that reshapes it costs the PvP relay probe, not the mod.
            try
            {
                _getPeerZdoRevision = CreatePeerZdoRevisionGetter(zdoPeerType);
            }
            catch (Exception)
            {
                _getPeerZdoRevision = null;
            }
        }

        public static HSteamNetConnection GetConnection(ZSteamSocket socket) => _getConnection(socket);
        public static Queue<byte[]> GetSendQueue(ZSteamSocket socket) => _getSendQueue(socket);
        public static void AddTotalSent(ZSteamSocket socket, int bytes) => _addTotalSent(socket, bytes);
        public static IList GetZdoPeers(ZDOMan manager) => _getZdoPeers(manager);
        public static ZNetPeer GetZNetPeer(object zdoPeer) => zdoPeer == null ? null : _getZNetPeer(zdoPeer);
        public static bool SendZdos(ZDOMan manager, object zdoPeer, bool flush) => _sendZdos(manager, zdoPeer, flush);
        public static bool PeerZdoRevisionAvailable => _getPeerZdoRevision != null;

        /// <summary>The data revision of <paramref name="id"/> the server last sent to this ZDOPeer.</summary>
        public static bool TryGetPeerZdoRevision(object zdoPeer, ZDOID id, out uint dataRevision)
        {
            dataRevision = 0;
            return zdoPeer != null && _getPeerZdoRevision != null && _getPeerZdoRevision(zdoPeer, id, out dataRevision);
        }

        private static Func<TTarget, TValue> CreateFieldGetter<TTarget, TValue>(string name)
        {
            FieldInfo field = RequireField(typeof(TTarget), name, typeof(TValue));
            var method = new DynamicMethod("AdaptiveNet_Get_" + name, typeof(TValue),
                new[] { typeof(TTarget) }, typeof(GameAccess), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TTarget, TValue>)method.CreateDelegate(typeof(Func<TTarget, TValue>));
        }

        private static Action<TTarget, int> CreateIntFieldAdder<TTarget>(string name)
        {
            FieldInfo field = RequireField(typeof(TTarget), name, typeof(int));
            var method = new DynamicMethod("AdaptiveNet_Add_" + name, null,
                new[] { typeof(TTarget), typeof(int) }, typeof(GameAccess), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<TTarget, int>)method.CreateDelegate(typeof(Action<TTarget, int>));
        }

        private static Func<ZDOMan, IList> CreateListGetter(string name)
        {
            FieldInfo field = typeof(ZDOMan).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || !typeof(IList).IsAssignableFrom(field.FieldType))
            {
                throw new MissingFieldException(typeof(ZDOMan).FullName, name);
            }

            var method = new DynamicMethod("AdaptiveNet_Get_" + name, typeof(IList),
                new[] { typeof(ZDOMan) }, typeof(GameAccess), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Castclass, typeof(IList));
            il.Emit(OpCodes.Ret);
            return (Func<ZDOMan, IList>)method.CreateDelegate(typeof(Func<ZDOMan, IList>));
        }

        private static Func<object, TValue> CreateObjectFieldGetter<TValue>(Type owner, string name)
        {
            FieldInfo field = RequireField(owner, name, typeof(TValue));
            var method = new DynamicMethod("AdaptiveNet_Get_ZDOPeer_" + name, typeof(TValue),
                new[] { typeof(object) }, typeof(GameAccess), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, TValue>)method.CreateDelegate(typeof(Func<object, TValue>));
        }

        private static Func<ZDOMan, object, bool, bool> CreateSendZdos(Type peerType)
        {
            MethodInfo target = typeof(ZDOMan).GetMethod("SendZDOs", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { peerType, typeof(bool) }, null);
            if (target == null || target.ReturnType != typeof(bool))
            {
                throw new MissingMethodException(typeof(ZDOMan).FullName, "SendZDOs");
            }

            var method = new DynamicMethod("AdaptiveNet_SendZDOs", typeof(bool),
                new[] { typeof(ZDOMan), typeof(object), typeof(bool) }, typeof(GameAccess), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, peerType);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            return (Func<ZDOMan, object, bool, bool>)method.CreateDelegate(
                typeof(Func<ZDOMan, object, bool, bool>));
        }

        /// <summary>
        /// ((ZDOPeer)peer).m_zdos.TryGetValue(id, out PeerZDOInfo info) and info.m_dataRevision,
        /// for the ZDOPeer and PeerZDOInfo types that are not visible to the mod.
        /// </summary>
        private static PeerZdoRevisionGetter CreatePeerZdoRevisionGetter(Type peerType)
        {
            FieldInfo zdos = peerType.GetField("m_zdos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Type infoType = peerType.GetNestedType("PeerZDOInfo", BindingFlags.Public | BindingFlags.NonPublic);
            if (zdos == null || infoType == null)
            {
                throw new MissingFieldException(peerType.FullName, "m_zdos");
            }

            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(ZDOID), infoType);
            if (zdos.FieldType != dictionaryType)
            {
                throw new MissingFieldException(peerType.FullName, "m_zdos");
            }
            FieldInfo dataRevision = RequireField(infoType, "m_dataRevision", typeof(uint));
            MethodInfo tryGetValue = dictionaryType.GetMethod(
                "TryGetValue", new[] { typeof(ZDOID), infoType.MakeByRefType() });
            if (tryGetValue == null)
            {
                throw new MissingMethodException(dictionaryType.FullName, "TryGetValue");
            }

            var method = new DynamicMethod("AdaptiveNet_GetPeerZdoRevision", typeof(bool),
                new[] { typeof(object), typeof(ZDOID), typeof(uint).MakeByRefType() }, typeof(GameAccess), true);
            ILGenerator il = method.GetILGenerator();
            LocalBuilder info = il.DeclareLocal(infoType);
            Label notFound = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, peerType);
            il.Emit(OpCodes.Ldfld, zdos);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloca_S, info);
            il.Emit(OpCodes.Callvirt, tryGetValue);
            il.Emit(OpCodes.Brfalse_S, notFound);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(infoType.IsValueType ? OpCodes.Ldloca_S : OpCodes.Ldloc_S, info);
            il.Emit(OpCodes.Ldfld, dataRevision);
            il.Emit(OpCodes.Stind_I4);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(notFound);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stind_I4);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            return (PeerZdoRevisionGetter)method.CreateDelegate(typeof(PeerZdoRevisionGetter));
        }

        private static FieldInfo RequireField(Type owner, string name, Type fieldType)
        {
            FieldInfo field = owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != fieldType)
            {
                throw new MissingFieldException(owner.FullName, name);
            }
            return field;
        }
    }
}
