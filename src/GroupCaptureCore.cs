#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AdaptiveNet.Core
{
    /// <summary>
    /// Captura de grupo sob demanda (admin): cada cliente com o mod devolve a propria serie de
    /// 1 amostra/s e o servidor junta tudo por segundo UTC. Este arquivo nao conhece Valheim nem
    /// Unity: so a serie, o formato binario, a coordenacao (prazo e limite de taxa) e o CSV.
    /// </summary>
    internal static class GroupCaptureLimits
    {
        public const int Protocol = 1;
        public const int DefaultSeconds = 60;
        public const int MaximumSeconds = 120;
        // Teto por cliente do bloco comprimido: 120 amostras de ~65 bytes deflatam para ~3 KiB.
        public const int MaximumCompressedBytes = 12 * 1024;
        public const int MaximumReplyPackageBytes = MaximumCompressedBytes + 64;
        // Mais que isso de relogio de diferenca entre cliente e servidor e corrigido na juncao.
        public const long SkewCorrectionThresholdMilliseconds = 2000L;

        public static int ClampSeconds(int requested)
        {
            if (requested <= 0) return DefaultSeconds;
            return Math.Min(MaximumSeconds, requested);
        }
    }

    /// <summary>O que um cliente viu em um segundo: so campos que o relatorio dele ja carrega.</summary>
    internal readonly struct GroupCaptureSample
    {
        public GroupCaptureSample(
            long utcSecond, bool focused, bool playerReady, bool teleporting, bool networkValid,
            int frames, double frameAvgMs, double frameP95Ms, double frameMaxMs, int stallFrames,
            int gen0, int gen1, int gen2, double gcCorrelatedFrameMs,
            int pingMs, double qualityLocal, double qualityRemote,
            double outBytesPerSecond, double inBytesPerSecond, double queueDelayMs,
            int unackedBytes, int transportRateBytesPerSecond,
            int aiInstances, double processCpuMsPerSecond, double systemCpuPercent, int processMemoryMiB)
        {
            UtcSecond = utcSecond;
            Focused = focused;
            PlayerReady = playerReady;
            Teleporting = teleporting;
            NetworkValid = networkValid;
            Frames = Math.Max(0, Math.Min(ushort.MaxValue, frames));
            FrameAvgMs = Clean(frameAvgMs);
            FrameP95Ms = Clean(frameP95Ms);
            FrameMaxMs = Clean(frameMaxMs);
            StallFrames = Math.Max(0, Math.Min(ushort.MaxValue, stallFrames));
            Gen0 = Math.Max(0, Math.Min(byte.MaxValue, gen0));
            Gen1 = Math.Max(0, Math.Min(byte.MaxValue, gen1));
            Gen2 = Math.Max(0, Math.Min(byte.MaxValue, gen2));
            GcCorrelatedFrameMs = Clean(gcCorrelatedFrameMs);
            PingMs = Math.Max(0, Math.Min(ushort.MaxValue, pingMs));
            QualityLocal = Math.Round(Math.Max(0d, Math.Min(1d, Finite(qualityLocal))) * 255d) / 255d;
            QualityRemote = Math.Round(Math.Max(0d, Math.Min(1d, Finite(qualityRemote))) * 255d) / 255d;
            OutBytesPerSecond = Clean(outBytesPerSecond);
            InBytesPerSecond = Clean(inBytesPerSecond);
            QueueDelayMs = Clean(queueDelayMs);
            UnackedBytes = Math.Max(0, unackedBytes);
            TransportRateBytesPerSecond = Math.Max(0, transportRateBytesPerSecond);
            AiInstances = Math.Max(-1, Math.Min(short.MaxValue, aiInstances));
            ProcessCpuMsPerSecond = processCpuMsPerSecond < 0d || double.IsNaN(processCpuMsPerSecond) ? -1d : Math.Min(100000d, processCpuMsPerSecond);
            SystemCpuPercent = systemCpuPercent < 0d || double.IsNaN(systemCpuPercent) ? -1d : Math.Min(100d, systemCpuPercent);
            ProcessMemoryMiB = Math.Max(-1, processMemoryMiB);
        }

        public long UtcSecond { get; }
        public bool Focused { get; }
        public bool PlayerReady { get; }
        public bool Teleporting { get; }
        public bool NetworkValid { get; }
        public int Frames { get; }
        public double FrameAvgMs { get; }
        public double FrameP95Ms { get; }
        public double FrameMaxMs { get; }
        public int StallFrames { get; }
        public int Gen0 { get; }
        public int Gen1 { get; }
        public int Gen2 { get; }
        public double GcCorrelatedFrameMs { get; }
        public int PingMs { get; }
        public double QualityLocal { get; }
        public double QualityRemote { get; }
        public double OutBytesPerSecond { get; }
        public double InBytesPerSecond { get; }
        public double QueueDelayMs { get; }
        public int UnackedBytes { get; }
        public int TransportRateBytesPerSecond { get; }
        /// <summary>-1 = nao medido.</summary>
        public int AiInstances { get; }
        public double ProcessCpuMsPerSecond { get; }
        public double SystemCpuPercent { get; }
        public int ProcessMemoryMiB { get; }

        public GroupCaptureSample WithUtcSecond(long utcSecond)
        {
            return new GroupCaptureSample(
                utcSecond, Focused, PlayerReady, Teleporting, NetworkValid, Frames, FrameAvgMs, FrameP95Ms,
                FrameMaxMs, StallFrames, Gen0, Gen1, Gen2, GcCorrelatedFrameMs, PingMs, QualityLocal,
                QualityRemote, OutBytesPerSecond, InBytesPerSecond, QueueDelayMs, UnackedBytes,
                TransportRateBytesPerSecond, AiInstances, ProcessCpuMsPerSecond, SystemCpuPercent, ProcessMemoryMiB);
        }

        public static GroupCaptureSample FromReport(
            long utcSecond, RuntimeHealthSample health, NetworkSample network,
            bool focused, bool playerReady, bool teleporting,
            ClientAiSample ai = default, ClientMachineSample machine = default)
        {
            return new GroupCaptureSample(
                utcSecond, focused, playerReady, teleporting, network.Valid,
                health.FrameCount, health.AverageFrameMilliseconds, health.P95FrameMilliseconds,
                health.MaximumFrameMilliseconds, health.StallFrames,
                health.Gen0Collections, health.Gen1Collections, health.Gen2Collections,
                health.GcCorrelatedStallMilliseconds,
                network.PingMilliseconds, network.LocalQuality, network.RemoteQuality,
                network.OutgoingBytesPerSecond, network.IncomingBytesPerSecond, network.QueueDelayMilliseconds,
                network.UnackedBytes, network.TransportSendRateBytesPerSecond,
                ai.Measured ? ai.Instances : -1,
                machine.Measured ? machine.ProcessCpuMsPerSecond : -1d,
                machine.Measured ? machine.SystemCpuPercent : -1d,
                machine.Measured ? machine.ProcessMemoryMiB : -1);
        }

        private static double Finite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;
        }

        private static double Clean(double value)
        {
            return Math.Max(0d, Math.Min(10000000d, Finite(value)));
        }
    }

    /// <summary>Os ultimos segundos do cliente, uma amostra por segundo UTC (a mais nova vence).</summary>
    internal sealed class GroupCaptureSeries
    {
        private readonly Queue<GroupCaptureSample> _samples = new Queue<GroupCaptureSample>();

        public int Count => _samples.Count;

        public void Add(GroupCaptureSample sample)
        {
            if (_samples.Count > 0)
            {
                GroupCaptureSample[] all = _samples.ToArray();
                if (all[all.Length - 1].UtcSecond >= sample.UtcSecond)
                {
                    // Mesmo segundo (relatorio a cada 0,5 s) ou relogio que voltou: so substitui o ultimo.
                    if (all[all.Length - 1].UtcSecond != sample.UtcSecond) return;
                    _samples.Clear();
                    for (int index = 0; index < all.Length - 1; index++) _samples.Enqueue(all[index]);
                }
            }
            _samples.Enqueue(sample);
            while (_samples.Count > GroupCaptureLimits.MaximumSeconds) _samples.Dequeue();
        }

        public GroupCaptureSample[] Take(long nowUtcSecond, int seconds)
        {
            long first = nowUtcSecond - GroupCaptureLimits.ClampSeconds(seconds);
            var result = new List<GroupCaptureSample>();
            foreach (GroupCaptureSample sample in _samples)
            {
                if (sample.UtcSecond > first && sample.UtcSecond <= nowUtcSecond + 1) result.Add(sample);
            }
            return result.ToArray();
        }
    }

    /// <summary>Bloco binario das amostras, deflate; tamanho de cada amostra e fixo.</summary>
    internal static class GroupCaptureCodec
    {
        public const int SampleBytes = 2 + 1 + 2 + 12 + 2 + 3 + 4 + 2 + 2 + 12 + 8 + 2 + 8 + 4;
        private const int HeaderBytes = 8 + 2;

        /// <summary>
        /// Comprime as amostras (ascendentes por tempo). Se passar do teto, descarta as mais antigas
        /// ate caber; devolve quantas amostras foram.
        /// </summary>
        public static byte[] Encode(GroupCaptureSample[] samples, int maximumCompressedBytes, out int included)
        {
            int count = Math.Min(samples?.Length ?? 0, GroupCaptureLimits.MaximumSeconds);
            int skip = (samples?.Length ?? 0) - count;
            while (true)
            {
                byte[] compressed = Compress(samples, skip, count);
                if (compressed.Length <= maximumCompressedBytes || count == 0)
                {
                    included = count;
                    return compressed.Length <= maximumCompressedBytes ? compressed : new byte[0];
                }
                int drop = Math.Max(1, count / 10);
                skip += drop;
                count -= drop;
            }
        }

        public static bool TryDecode(byte[] blob, out GroupCaptureSample[] samples)
        {
            samples = new GroupCaptureSample[0];
            if (blob == null || blob.Length == 0 || blob.Length > GroupCaptureLimits.MaximumCompressedBytes) return false;
            try
            {
                int limit = HeaderBytes + GroupCaptureLimits.MaximumSeconds * SampleBytes;
                byte[] raw;
                using (var input = new MemoryStream(blob))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[2048];
                    int read;
                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > limit) return false;
                        output.Write(buffer, 0, read);
                    }
                    raw = output.ToArray();
                }
                using (var reader = new BinaryReader(new MemoryStream(raw)))
                {
                    long baseSecond = reader.ReadInt64();
                    int count = reader.ReadUInt16();
                    if (count > GroupCaptureLimits.MaximumSeconds) return false;
                    if (raw.Length != HeaderBytes + count * SampleBytes) return false;
                    var result = new GroupCaptureSample[count];
                    for (int index = 0; index < count; index++)
                    {
                        long utc = baseSecond + reader.ReadUInt16();
                        byte flags = reader.ReadByte();
                        int frames = reader.ReadUInt16();
                        double avg = reader.ReadSingle();
                        double p95 = reader.ReadSingle();
                        double max = reader.ReadSingle();
                        int stalls = reader.ReadUInt16();
                        int g0 = reader.ReadByte();
                        int g1 = reader.ReadByte();
                        int g2 = reader.ReadByte();
                        double gcMs = reader.ReadSingle();
                        int ping = reader.ReadUInt16();
                        double ql = reader.ReadByte() / 255d;
                        double qr = reader.ReadByte() / 255d;
                        double outBps = reader.ReadSingle();
                        double inBps = reader.ReadSingle();
                        double queue = reader.ReadSingle();
                        int unacked = reader.ReadInt32();
                        int rate = reader.ReadInt32();
                        int ai = reader.ReadInt16();
                        double procCpu = reader.ReadSingle();
                        double sysCpu = reader.ReadSingle();
                        int mem = reader.ReadInt32();
                        result[index] = new GroupCaptureSample(
                            utc, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0,
                            frames, avg, p95, max, stalls, g0, g1, g2, gcMs, ping, ql, qr,
                            outBps, inBps, queue, unacked, rate, ai, procCpu, sysCpu, mem);
                    }
                    samples = result;
                    return true;
                }
            }
            catch
            {
                samples = new GroupCaptureSample[0];
                return false;
            }
        }

        private static byte[] Compress(GroupCaptureSample[] samples, int skip, int count)
        {
            using (var raw = new MemoryStream())
            using (var writer = new BinaryWriter(raw))
            {
                long baseSecond = count > 0 ? samples[skip].UtcSecond : 0L;
                writer.Write(baseSecond);
                writer.Write((ushort)count);
                for (int index = skip; index < skip + count; index++)
                {
                    GroupCaptureSample s = samples[index];
                    writer.Write((ushort)Math.Max(0L, Math.Min(ushort.MaxValue, s.UtcSecond - baseSecond)));
                    writer.Write((byte)((s.Focused ? 1 : 0) | (s.PlayerReady ? 2 : 0) | (s.Teleporting ? 4 : 0) | (s.NetworkValid ? 8 : 0)));
                    writer.Write((ushort)s.Frames);
                    writer.Write((float)s.FrameAvgMs);
                    writer.Write((float)s.FrameP95Ms);
                    writer.Write((float)s.FrameMaxMs);
                    writer.Write((ushort)s.StallFrames);
                    writer.Write((byte)s.Gen0);
                    writer.Write((byte)s.Gen1);
                    writer.Write((byte)s.Gen2);
                    writer.Write((float)s.GcCorrelatedFrameMs);
                    writer.Write((ushort)s.PingMs);
                    writer.Write((byte)Math.Round(s.QualityLocal * 255d));
                    writer.Write((byte)Math.Round(s.QualityRemote * 255d));
                    writer.Write((float)s.OutBytesPerSecond);
                    writer.Write((float)s.InBytesPerSecond);
                    writer.Write((float)s.QueueDelayMs);
                    writer.Write(s.UnackedBytes);
                    writer.Write(s.TransportRateBytesPerSecond);
                    writer.Write((short)s.AiInstances);
                    writer.Write((float)s.ProcessCpuMsPerSecond);
                    writer.Write((float)s.SystemCpuPercent);
                    writer.Write(s.ProcessMemoryMiB);
                }
                writer.Flush();
                using (var packed = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, true))
                    {
                        deflate.Write(raw.GetBuffer(), 0, (int)raw.Length);
                    }
                    return packed.ToArray();
                }
            }
        }
    }

    internal enum GroupCapturePeerState { Pending, Replied, NoMod, TimedOut }

    internal sealed class GroupCapturePeer
    {
        public long Uid { get; set; }
        public string Name { get; set; } = string.Empty;
        public GroupCapturePeerState State { get; set; }
        public GroupCaptureSample[] Samples { get; set; } = new GroupCaptureSample[0];
        public int SkewSeconds { get; set; }
        public int ReplyBytes { get; set; }
    }

    internal sealed class GroupCaptureResult
    {
        public int CaptureId { get; set; }
        public string RequestedBy { get; set; } = string.Empty;
        public long StartUtcSecond { get; set; }
        public int Seconds { get; set; }
        public double TimeoutSeconds { get; set; }
        public List<GroupCapturePeer> Peers { get; } = new List<GroupCapturePeer>();
    }

    internal readonly struct GroupCaptureParticipant
    {
        public GroupCaptureParticipant(long uid, string name, bool hasMod)
        {
            Uid = uid;
            Name = name ?? "unknown";
            HasMod = hasMod;
        }

        public long Uid { get; }
        public string Name { get; }
        public bool HasMod { get; }
    }

    /// <summary>Uma captura por vez, prazo para as respostas e intervalo minimo entre capturas.</summary>
    internal sealed class GroupCaptureCoordinator
    {
        private readonly double _timeoutSeconds;
        private readonly double _minimumIntervalSeconds;
        private double _nextAllowedTime = double.NegativeInfinity;
        private double _deadline;
        private int _nextId = 1;
        private GroupCaptureResult _active;

        public GroupCaptureCoordinator(double timeoutSeconds, double minimumIntervalSeconds)
        {
            _timeoutSeconds = Math.Max(1d, timeoutSeconds);
            _minimumIntervalSeconds = Math.Max(0d, minimumIntervalSeconds);
        }

        public bool Active => _active != null;
        public int ActiveId => _active?.CaptureId ?? 0;

        public bool TryBegin(
            double now, string requestedBy, int seconds, long serverUtcSecond,
            IEnumerable<GroupCaptureParticipant> participants, out GroupCaptureResult capture, out string refusal)
        {
            capture = null;
            refusal = string.Empty;
            if (_active != null)
            {
                refusal = "a capture is already running";
                return false;
            }
            if (now < _nextAllowedTime)
            {
                refusal = $"rate limit: next capture in {_nextAllowedTime - now:F0} s";
                return false;
            }
            _nextAllowedTime = now + _minimumIntervalSeconds;
            _deadline = now + _timeoutSeconds;
            var result = new GroupCaptureResult
            {
                CaptureId = _nextId++,
                RequestedBy = requestedBy ?? string.Empty,
                StartUtcSecond = serverUtcSecond,
                Seconds = GroupCaptureLimits.ClampSeconds(seconds),
                TimeoutSeconds = _timeoutSeconds
            };
            foreach (GroupCaptureParticipant participant in participants)
            {
                result.Peers.Add(new GroupCapturePeer
                {
                    Uid = participant.Uid,
                    Name = participant.Name,
                    State = participant.HasMod ? GroupCapturePeerState.Pending : GroupCapturePeerState.NoMod
                });
            }
            _active = result;
            capture = result;
            return true;
        }

        /// <summary>
        /// Aceita a resposta de um cliente que ainda deve uma. O relogio dele e corrigido quando
        /// diverge do servidor por mais que o limite (a latencia de ida fica abaixo dele).
        /// </summary>
        public bool AcceptReply(long uid, int captureId, GroupCaptureSample[] samples, long clientUtcMilliseconds, long serverUtcMilliseconds, int replyBytes)
        {
            if (_active == null || _active.CaptureId != captureId || samples == null) return false;
            foreach (GroupCapturePeer peer in _active.Peers)
            {
                if (peer.Uid != uid) continue;
                if (peer.State != GroupCapturePeerState.Pending) return false;
                long skewMilliseconds = serverUtcMilliseconds - clientUtcMilliseconds;
                int skewSeconds = Math.Abs(skewMilliseconds) >= GroupCaptureLimits.SkewCorrectionThresholdMilliseconds
                    ? (int)Math.Round(skewMilliseconds / 1000d)
                    : 0;
                var shifted = new GroupCaptureSample[samples.Length];
                for (int index = 0; index < samples.Length; index++) shifted[index] = samples[index].WithUtcSecond(samples[index].UtcSecond + skewSeconds);
                peer.Samples = shifted;
                peer.SkewSeconds = skewSeconds;
                peer.ReplyBytes = replyBytes;
                peer.State = GroupCapturePeerState.Replied;
                return true;
            }
            return false;
        }

        /// <summary>Devolve o resultado quando todos responderam ou o prazo acabou; senao null.</summary>
        public GroupCaptureResult Poll(double now)
        {
            if (_active == null) return null;
            bool pending = false;
            foreach (GroupCapturePeer peer in _active.Peers)
            {
                if (peer.State == GroupCapturePeerState.Pending) pending = true;
            }
            if (pending && now < _deadline) return null;
            foreach (GroupCapturePeer peer in _active.Peers)
            {
                if (peer.State == GroupCapturePeerState.Pending) peer.State = GroupCapturePeerState.TimedOut;
            }
            GroupCaptureResult done = _active;
            _active = null;
            return done;
        }
    }

    internal static class GroupCaptureCsv
    {
        public const string Header =
            "utc,utc_epoch,player,uid,skew_s,focused,player_ready,teleporting,frames,frame_avg_ms,frame_p95_ms,frame_max_ms," +
            "stall_frames,gc0,gc1,gc2,gc_correlated_frame_ms,ping_ms,quality_local,quality_remote,network_valid," +
            "out_bps,in_bps,queue_delay_ms,unacked_bytes,transport_rate_bps,ai_instances,process_cpu_ms_s,system_cpu_pct,process_memory_mib";

        public static int RowCount(GroupCaptureResult result)
        {
            int rows = 0;
            foreach (GroupCapturePeer peer in result.Peers)
            {
                if (peer.State == GroupCapturePeerState.Replied) rows += peer.Samples.Length;
            }
            return rows;
        }

        public static string Format(GroupCaptureResult result)
        {
            var text = new StringBuilder();
            text.Append("# group-capture id=").Append(result.CaptureId.ToString(CultureInfo.InvariantCulture))
                .Append(" requested_by=").Append(Clean(result.RequestedBy))
                .Append(" start_utc=").Append(Iso(result.StartUtcSecond))
                .Append(" seconds=").Append(result.Seconds.ToString(CultureInfo.InvariantCulture))
                .Append(" timeout_s=").Append(result.TimeoutSeconds.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
            AppendList(text, "responded", result, GroupCapturePeerState.Replied);
            AppendList(text, "no_mod", result, GroupCapturePeerState.NoMod);
            AppendList(text, "timed_out", result, GroupCapturePeerState.TimedOut);
            text.Append(Header).Append('\n');

            var rows = new List<KeyValuePair<GroupCapturePeer, GroupCaptureSample>>();
            foreach (GroupCapturePeer peer in result.Peers)
            {
                if (peer.State != GroupCapturePeerState.Replied) continue;
                foreach (GroupCaptureSample sample in peer.Samples) rows.Add(new KeyValuePair<GroupCapturePeer, GroupCaptureSample>(peer, sample));
            }
            rows.Sort((left, right) =>
            {
                int byTime = left.Value.UtcSecond.CompareTo(right.Value.UtcSecond);
                return byTime != 0 ? byTime : left.Key.Uid.CompareTo(right.Key.Uid);
            });
            foreach (KeyValuePair<GroupCapturePeer, GroupCaptureSample> row in rows)
            {
                GroupCaptureSample s = row.Value;
                var c = CultureInfo.InvariantCulture;
                text.Append(Iso(s.UtcSecond)).Append(',')
                    .Append(s.UtcSecond.ToString(c)).Append(',')
                    .Append(Clean(row.Key.Name)).Append(',')
                    .Append(row.Key.Uid.ToString(c)).Append(',')
                    .Append(row.Key.SkewSeconds.ToString(c)).Append(',')
                    .Append(s.Focused ? 1 : 0).Append(',')
                    .Append(s.PlayerReady ? 1 : 0).Append(',')
                    .Append(s.Teleporting ? 1 : 0).Append(',')
                    .Append(s.Frames.ToString(c)).Append(',')
                    .Append(s.FrameAvgMs.ToString("F2", c)).Append(',')
                    .Append(s.FrameP95Ms.ToString("F2", c)).Append(',')
                    .Append(s.FrameMaxMs.ToString("F2", c)).Append(',')
                    .Append(s.StallFrames.ToString(c)).Append(',')
                    .Append(s.Gen0.ToString(c)).Append(',')
                    .Append(s.Gen1.ToString(c)).Append(',')
                    .Append(s.Gen2.ToString(c)).Append(',')
                    .Append(s.GcCorrelatedFrameMs.ToString("F2", c)).Append(',')
                    .Append(s.PingMs.ToString(c)).Append(',')
                    .Append(s.QualityLocal.ToString("F2", c)).Append(',')
                    .Append(s.QualityRemote.ToString("F2", c)).Append(',')
                    .Append(s.NetworkValid ? 1 : 0).Append(',')
                    .Append(s.OutBytesPerSecond.ToString("F0", c)).Append(',')
                    .Append(s.InBytesPerSecond.ToString("F0", c)).Append(',')
                    .Append(s.QueueDelayMs.ToString("F2", c)).Append(',')
                    .Append(s.UnackedBytes.ToString(c)).Append(',')
                    .Append(s.TransportRateBytesPerSecond.ToString(c)).Append(',')
                    .Append(s.AiInstances.ToString(c)).Append(',')
                    .Append(s.ProcessCpuMsPerSecond.ToString("F1", c)).Append(',')
                    .Append(s.SystemCpuPercent.ToString("F1", c)).Append(',')
                    .Append(s.ProcessMemoryMiB.ToString(c)).Append('\n');
            }
            return text.ToString();
        }

        private static void AppendList(StringBuilder text, string label, GroupCaptureResult result, GroupCapturePeerState state)
        {
            text.Append("# ").Append(label).Append('=');
            bool first = true;
            foreach (GroupCapturePeer peer in result.Peers)
            {
                if (peer.State != state) continue;
                if (!first) text.Append(';');
                first = false;
                text.Append(Clean(peer.Name)).Append('(').Append(peer.Uid.ToString(CultureInfo.InvariantCulture));
                if (state == GroupCapturePeerState.Replied)
                {
                    text.Append(" samples=").Append(peer.Samples.Length.ToString(CultureInfo.InvariantCulture))
                        .Append(" bytes=").Append(peer.ReplyBytes.ToString(CultureInfo.InvariantCulture))
                        .Append(" skew_s=").Append(peer.SkewSeconds.ToString(CultureInfo.InvariantCulture));
                }
                text.Append(')');
            }
            text.Append('\n');
        }

        private static string Iso(long utcSecond)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(utcSecond).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return "invalid";
            }
        }

        // O nome vem do jogador: nada que quebre linha/coluna nem vire formula numa planilha.
        internal static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            var text = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                text.Append(char.IsControl(character) || character == ',' || character == ';' || character == '"' || character == '(' || character == ')' ? '_' : character);
            }
            if ("=+-@#".IndexOf(text[0]) >= 0) text[0] = '_';
            return text.ToString();
        }
    }
}
