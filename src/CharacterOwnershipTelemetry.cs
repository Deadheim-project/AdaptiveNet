using System;

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

    /// <summary>
    /// Resultado do censo de donos (ServerOwnershipCensus). Antes contava Character.GetAllCharacters(),
    /// que no servidor dedicado so ve objetos instanciados la e dava sempre 0; agora vem da
    /// contagem por ZDO nos setores ao redor de cada jogador.
    /// </summary>
    internal static class CharacterOwnershipTelemetry
    {
        public static readonly OwnershipTally Tally = new OwnershipTally();

        public static int ActiveNonPlayerCharacters => Tally.Active;
        public static int UnownedNonPlayerCharacters => Tally.Unowned;
        public static int ServerOwnedNonPlayerCharacters => Tally.ServerOwned;

        public static CharacterOwnershipSnapshot Get(long owner) => Tally.Get(owner);

        public static void Reset() => Tally.Reset();
    }
}
