using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AdaptiveNet
{
    internal readonly struct CharacterOwnershipSnapshot
    {
        public CharacterOwnershipSnapshot(
            int ownedNonPlayerCharacters,
            int changeSincePreviousScan,
            int transfersSincePreviousScan = 0,
            long scanId = 0L)
        {
            OwnedNonPlayerCharacters = Math.Max(0, ownedNonPlayerCharacters);
            ChangeSincePreviousScan = Math.Max(0, changeSincePreviousScan);
            TransfersSincePreviousScan = Math.Max(0, transfersSincePreviousScan);
            ScanId = Math.Max(0L, scanId);
        }

        public int OwnedNonPlayerCharacters { get; }
        public int ChangeSincePreviousScan { get; }
        public int TransfersSincePreviousScan { get; }
        public long ScanId { get; }
    }

    internal static class CharacterOwnershipTelemetry
    {
        private static readonly Dictionary<long, int> Current = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> Previous = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> Changes = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> Transfers = new Dictionary<long, int>();
        private static readonly Dictionary<Character, long> CurrentOwners =
            new Dictionary<Character, long>(ReferenceComparer<Character>.Instance);
        private static readonly Dictionary<Character, long> PreviousOwners =
            new Dictionary<Character, long>(ReferenceComparer<Character>.Instance);
        private static bool _initialized;
        private static long _scanId;

        public static int ActiveNonPlayerCharacters { get; private set; }
        public static int UnownedNonPlayerCharacters { get; private set; }
        public static int ServerOwnedNonPlayerCharacters { get; private set; }

        public static void Refresh()
        {
            _scanId++;
            Previous.Clear();
            foreach (KeyValuePair<long, int> pair in Current) Previous[pair.Key] = pair.Value;
            Current.Clear();
            Changes.Clear();
            Transfers.Clear();
            PreviousOwners.Clear();
            foreach (KeyValuePair<Character, long> pair in CurrentOwners) PreviousOwners[pair.Key] = pair.Value;
            CurrentOwners.Clear();
            ActiveNonPlayerCharacters = 0;
            UnownedNonPlayerCharacters = 0;
            ServerOwnedNonPlayerCharacters = 0;

            List<Character> characters = Character.GetAllCharacters();
            long serverUid = ZDOMan.instance != null ? ZDOMan.GetSessionID() : 0L;
            for (int index = 0; index < characters.Count; index++)
            {
                Character character = characters[index];
                if (character == null || character.IsPlayer()) continue;
                ActiveNonPlayerCharacters++;
                long owner = character.GetOwner();
                CurrentOwners[character] = owner;
                if (owner == 0L)
                {
                    UnownedNonPlayerCharacters++;
                    continue;
                }
                if (owner == serverUid) ServerOwnedNonPlayerCharacters++;
                Current.TryGetValue(owner, out int count);
                Current[owner] = count + 1;
            }

            if (!_initialized)
            {
                _initialized = true;
                return;
            }

            foreach (KeyValuePair<long, int> pair in Current)
            {
                Previous.TryGetValue(pair.Key, out int oldCount);
                Changes[pair.Key] = Math.Abs(pair.Value - oldCount);
            }
            foreach (KeyValuePair<long, int> pair in Previous)
            {
                if (!Current.ContainsKey(pair.Key)) Changes[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<Character, long> pair in CurrentOwners)
            {
                if (!PreviousOwners.TryGetValue(pair.Key, out long previousOwner) || previousOwner == pair.Value)
                {
                    continue;
                }

                if (previousOwner != 0L) Increment(Transfers, previousOwner);
                if (pair.Value != 0L) Increment(Transfers, pair.Value);
            }
        }

        public static CharacterOwnershipSnapshot Get(long owner)
        {
            Current.TryGetValue(owner, out int count);
            Changes.TryGetValue(owner, out int change);
            Transfers.TryGetValue(owner, out int transfers);
            return new CharacterOwnershipSnapshot(count, change, transfers, _scanId);
        }

        public static void Reset()
        {
            Current.Clear();
            Previous.Clear();
            Changes.Clear();
            Transfers.Clear();
            CurrentOwners.Clear();
            PreviousOwners.Clear();
            ActiveNonPlayerCharacters = 0;
            UnownedNonPlayerCharacters = 0;
            ServerOwnedNonPlayerCharacters = 0;
            _initialized = false;
            _scanId = 0L;
        }

        private static void Increment(Dictionary<long, int> values, long key)
        {
            values.TryGetValue(key, out int count);
            values[key] = count + 1;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T left, T right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
