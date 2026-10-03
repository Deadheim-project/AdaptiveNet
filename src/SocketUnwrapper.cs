using System;
using System.Collections.Generic;
using System.Reflection;

namespace AdaptiveNet
{
    /// <summary>
    /// Finds the socket that really carries a peer's traffic.
    /// </summary>
    /// <remarks>
    /// On a crossplay server ServerSync replaces peer.m_socket during login with a
    /// BufferingSocket — a ZPlayFabSocket subclass that keeps the real socket in an
    /// <c>Original</c> field — and puts the original back once the configs are sent. Every mod
    /// in the pack carries its own copy of ServerSync (eleven in Deadheim): each copy wraps the
    /// one before it, and each puts back only a wrapper of its own type, and only if that one is
    /// outermost when it finishes. They finish in any order, so a peer usually keeps a wrapper
    /// for its whole session.
    ///
    /// Seen through the wrapper a Steam link is not a ZSteamSocket, so it got no Steam sample
    /// and no limits, and the vanilla fallback reached ZSteamSocket.GetConnectionQuality, which
    /// the dedicated build leaves on the client Steam interface and which therefore throws.
    /// Verified on the live server on 2026-10-03: a Steam player sampled as "Steamworks is not
    /// initialized" for a whole session.
    ///
    /// Any socket type with an ISocket field named Original counts as a wrapper, so the
    /// ServerCharacters copy, which has its own BufferingSocket, is unwrapped too.
    /// </remarks>
    internal static class SocketUnwrapper
    {
        private const int MaximumDepth = 32;
        private static readonly Dictionary<Type, FieldInfo> OriginalFields = new Dictionary<Type, FieldInfo>();

        public static ISocket Unwrap(ISocket socket)
        {
            for (int depth = 0; socket != null && depth < MaximumDepth; depth++)
            {
                FieldInfo original = GetOriginalField(socket.GetType());
                if (original == null ||
                    !(original.GetValue(socket) is ISocket inner) ||
                    ReferenceEquals(inner, socket))
                {
                    return socket;
                }
                socket = inner;
            }
            return socket;
        }

        private static FieldInfo GetOriginalField(Type type)
        {
            if (OriginalFields.TryGetValue(type, out FieldInfo field)) return field;
            field = type.GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && !typeof(ISocket).IsAssignableFrom(field.FieldType)) field = null;
            OriginalFields[type] = field;
            return field;
        }
    }
}
