using System.Diagnostics;
using QuickMatch.Voice;
using Xunit;
using Xunit.Abstractions;

namespace QuickMatch.Tests;

/// <summary>Voice chat's plain C# parts: codec, packets, encryption, gate, jitter buffer, limiter, mutes and a whole session.</summary>
public class VoiceTests
{
    private const ulong Alice = 76561198000000011;
    private const ulong Bob = 76561198000000012;
    private const ulong Carol = 76561198000000013;
    private const int N = VoiceFormat.FrameSamples;

    private readonly ITestOutputHelper _output;

    public VoiceTests(ITestOutputHelper output) => _output = output;

    // ---------- helpers ----------

    /// <summary>A voice-like test tone: two harmonics, given level in dB below full scale.</summary>
    private static short[] Tone(int frameIndex, double levelDb, double hz = 220)
    {
        var frame = new short[N];
        double amplitude = Math.Pow(10, levelDb / 20) * 32767 * Math.Sqrt(2) / 1.25;
        for (int i = 0; i < N; i++)
        {
            double t = (frameIndex * N + i) / (double)VoiceFormat.SampleRate;
            frame[i] = (short)Math.Clamp(amplitude * (Math.Sin(2 * Math.PI * hz * t) + 0.5 * Math.Sin(2 * Math.PI * hz * 3 * t)) / 1.0, short.MinValue, short.MaxValue);
        }
        return frame;
    }

    private static double Db(ReadOnlySpan<short> samples) => VoiceGate.Level(samples);

    private static VoicePacket Packet(uint first, int count = 3, bool starts = false, bool ends = false, uint stream = 7) =>
        new(stream, first, starts, ends, Enumerable.Range(0, count).Select(i => new[] { (byte)(first + i), (byte)1 }).ToArray());

    private sealed class FakeDevice : IVoiceDevice
    {
        public readonly Queue<short> Samples = new();
        public int PermissionRequests;
        public int Starts;
        public bool Capturing;

        public VoiceCaptureState CaptureState { get; set; } = VoiceCaptureState.Ready;

        public IVoiceOutput? Output => null;

        public void RequestPermission()
        {
            PermissionRequests++;
            CaptureState = VoiceCaptureState.Asking;
        }

        public bool StartCapture()
        {
            Starts++;
            Capturing = true;
            return true;
        }

        public void StopCapture() => Capturing = false;

        public int ReadCapture(Span<short> destination)
        {
            int count = 0;
            while (count < destination.Length && Samples.Count > 0)
            {
                destination[count++] = Samples.Dequeue();
            }
            return count;
        }

        public void Feed(short[] frame)
        {
            if (Capturing)
            {
                foreach (short sample in frame)
                {
                    Samples.Enqueue(sample);
                }
            }
        }
    }

    private sealed class FakeOutput : IVoiceOutput
    {
        public readonly List<short> Played = new();
        public int Queued;
        public int Stops;

        public int QueuedSamples => Queued;

        public void Play(ReadOnlySpan<short> samples)
        {
            Played.AddRange(samples.ToArray());
            Queued += samples.Length;
        }

        public void Stop()
        {
            Stops++;
            Queued = 0;
        }

        /// <summary>20 ms of the queue was heard.</summary>
        public void Tick() => Queued = Math.Max(0, Queued - N);
    }

    /// <summary>Delivers packets to the other sessions through real encryption, after a delay in ticks.</summary>
    private sealed class FakeLink : IVoiceLink
    {
        private readonly ulong _from;
        private readonly VoiceCrypto _crypto;
        private readonly List<(int Due, byte[] Payload)> _inFlight = new();
        public readonly List<VoiceSession> To = new();
        /// <summary>Audio packets sent; status packets are counted apart.</summary>
        public int Sent;
        public int StatusSent;
        public int DelayTicks;
        public int Tick;

        public FakeLink(ulong from, byte[] key)
        {
            _from = from;
            _crypto = new VoiceCrypto(key);
        }

        public bool IsUp { get; set; } = true;

        public void Send(VoicePacket packet)
        {
            if (packet.Status != null)
            {
                StatusSent++;
            }
            else
            {
                Sent++;
            }
            _inFlight.Add((Tick + DelayTicks, _crypto.Seal(packet)));
        }

        public void Deliver(VoiceCrypto receiverCrypto)
        {
            foreach (var item in _inFlight.Where(p => p.Due <= Tick).ToList())
            {
                _inFlight.Remove(item);
                VoicePacket? packet = receiverCrypto.Open(item.Payload);
                Assert.NotNull(packet);
                foreach (VoiceSession session in To)
                {
                    session.Receive(_from, packet!);
                }
            }
            Tick++;
        }
    }

    // ---------- codec ----------

    [Fact]
    public void Codec_round_trip_keeps_the_level_of_speech_like_sound()
    {
        var encoder = new VoiceEncoder();
        var decoder = new VoiceDecoder();
        var pcm = new short[N];
        var sizes = new List<int>();
        var stopwatch = Stopwatch.StartNew();
        const int frames = 100;
        double inDb = 0, outDb = 0;
        for (int f = 0; f < frames; f++)
        {
            short[] frame = Tone(f, -20);
            byte[] encoded = encoder.Encode(frame);
            sizes.Add(encoded.Length);
            decoder.Decode(encoded, pcm);
            if (f >= 10)
            {
                inDb += Db(frame);
                outDb += Db(pcm);
            }
        }
        stopwatch.Stop();
        inDb /= frames - 10;
        outDb /= frames - 10;
        _output.WriteLine($"in {inDb:F1} dB, out {outDb:F1} dB, frame bytes {sizes.Min()}-{sizes.Max()} (mean {sizes.Average():F0}), " +
            $"{stopwatch.Elapsed.TotalMilliseconds / frames:F3} ms per encode and decode");
        Assert.InRange(outDb, inDb - 3, inDb + 3);
        Assert.True(sizes.Max() <= VoiceFormat.MaxFrameBytes);
        Assert.InRange(sizes.Average(), 20, 80);
    }

    [Fact]
    public void Decoder_conceals_lost_and_garbage_frames_without_throwing()
    {
        var encoder = new VoiceEncoder();
        var decoder = new VoiceDecoder();
        var pcm = new short[N];
        for (int f = 0; f < 10; f++)
        {
            decoder.Decode(encoder.Encode(Tone(f, -20)), pcm);
        }
        decoder.Conceal(pcm, next: default);
        decoder.Conceal(pcm, encoder.Encode(Tone(11, -20)));
        decoder.Decode(new byte[] { 0xff, 0xff, 0xff, 0x00, 0x13 }, pcm);
        decoder.Decode(Enumerable.Repeat((byte)0xfc, 300).ToArray(), pcm);
        decoder.Decode(encoder.Encode(Tone(12, -20)), pcm);
    }

    // ---------- packets ----------

    [Fact]
    public void Packet_frames_round_trip()
    {
        var packet = new VoicePacket(42, 1000, StartsTalk: true, EndsTalk: false, new[] { new byte[] { 1, 2, 3 }, new byte[] { 4 }, new byte[] { 5, 6 } });
        VoicePacket? read = VoicePacket.ReadFrames(42, 1000, packet.WriteFrames());
        Assert.NotNull(read);
        Assert.True(read!.StartsTalk);
        Assert.False(read.EndsTalk);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, read.Frames.SelectMany(f => f).ToArray());
    }

    [Fact]
    public void Status_packets_round_trip_and_carry_no_audio()
    {
        VoicePacket? read = VoicePacket.ReadFrames(3, 8, VoicePacket.ForStatus(3, 8, VoiceStatus.ListenOnly).WriteFrames());
        Assert.Equal(VoiceStatus.ListenOnly, read!.Status);
        Assert.Empty(read.Frames);
        Assert.Null(VoicePacket.ReadFrames(3, 8, new byte[] { 4, 1, 1, 0, 9 }));
        Assert.Null(VoicePacket.ReadFrames(3, 8, new byte[] { 5, 1, 1, 0, 1 }));
        Assert.Null(VoicePacket.ReadFrames(3, 8, new byte[] { 4, 1, 1, 0, 1, 0 }));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0 })] // no frames
    [InlineData(new byte[] { 0, 7 })] // more than the most frames a packet may carry
    [InlineData(new byte[] { 4, 1, 1, 0, 9 })] // unknown flag
    [InlineData(new byte[] { 0, 1, 0, 0 })] // an empty frame
    [InlineData(new byte[] { 0, 1, 2, 0, 9 })] // a frame longer than the bytes left
    [InlineData(new byte[] { 0, 1, 1, 0, 9, 9 })] // bytes left over
    public void Malformed_packets_are_refused(byte[] part) => Assert.Null(VoicePacket.ReadFrames(1, 1, part));

    [Fact]
    public void Oversized_frames_are_refused()
    {
        var part = new byte[4 + VoiceFormat.MaxFrameBytes + 1];
        part[1] = 1;
        part[2] = (byte)((VoiceFormat.MaxFrameBytes + 1) & 0xff);
        part[3] = (byte)((VoiceFormat.MaxFrameBytes + 1) >> 8);
        Assert.Null(VoicePacket.ReadFrames(1, 1, part));
    }

    // ---------- encryption ----------

    [Fact]
    public void Voice_opens_only_with_the_senders_key()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var alice = new VoiceCrypto(key);
        using var bob = new VoiceCrypto(key);
        using var stranger = new VoiceCrypto(Enumerable.Repeat((byte)9, 32).ToArray());
        byte[] payload = alice.Seal(Packet(300, starts: true));

        Assert.True(VoiceCrypto.IsVoice(payload));
        Assert.False(VoiceCrypto.IsVoice("QC\u0001"u8));
        VoicePacket? opened = bob.Open(payload);
        Assert.NotNull(opened);
        Assert.Equal(300u, opened!.FirstFrame);
        Assert.True(opened.StartsTalk);
        Assert.Null(stranger.Open(payload));

        foreach (int at in new[] { 4, 8, VoiceCrypto.HeaderSize + 1, payload.Length - 1 })
        {
            byte[] changed = (byte[])payload.Clone();
            changed[at] ^= 1;
            Assert.Null(bob.Open(changed));
        }
        Assert.Null(bob.Open(new byte[VoiceCrypto.MaxPayloadBytes + 1]));
    }

    [Fact]
    public void Voice_nonces_never_repeat()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
        using var crypto = new VoiceCrypto(key);
        byte[] first = crypto.Seal(Packet(0));
        byte[] second = crypto.Seal(Packet(3));
        Assert.NotEqual(first.AsSpan(0, VoiceCrypto.HeaderSize).ToArray(), second.AsSpan(0, VoiceCrypto.HeaderSize).ToArray());
        // Same frames, next packet: the ciphertext differs because the nonce does.
        Assert.NotEqual(first.AsSpan(VoiceCrypto.HeaderSize).ToArray(), second.AsSpan(VoiceCrypto.HeaderSize).ToArray());
    }

    // ---------- gate ----------

    [Fact]
    public void Gate_opens_on_speech_holds_briefly_and_closes_on_silence()
    {
        var gate = new VoiceGate();
        for (int f = 0; f < 50; f++)
        {
            Assert.False(gate.Process(Tone(f, -70)));
        }
        Assert.True(gate.Process(Tone(50, -25)));
        for (int f = 0; f < VoiceGate.HangoverFrames; f++)
        {
            Assert.True(gate.Process(Tone(f, -70)));
        }
        Assert.False(gate.Process(Tone(99, -70)));
    }

    [Fact]
    public void Sensitivity_decides_whether_quiet_speech_opens_the_gate()
    {
        var sensitive = new VoiceGate { Sensitivity = 1 };
        var strict = new VoiceGate { Sensitivity = 0 };
        for (int f = 0; f < 100; f++)
        {
            sensitive.Process(Tone(f, -50));
            strict.Process(Tone(f, -50));
        }
        // 10 dB above the background: enough at sensitivity 1 (6 dB), not at 0 (18 dB).
        Assert.True(sensitive.Process(Tone(100, -40)));
        Assert.False(strict.Process(Tone(100, -40)));
    }

    [Fact]
    public void Loud_speech_opens_the_gate_even_in_a_noisy_room()
    {
        var gate = new VoiceGate { Sensitivity = 0 };
        for (int f = 0; f < 3000; f++)
        {
            gate.Process(Tone(f, -25, hz: 1000));
        }
        Assert.True(gate.Process(Tone(0, -10)));
    }

    // ---------- jitter buffer ----------

    private static List<Pull> TakeAll(JitterBuffer buffer, int count) => Enumerable.Range(0, count).Select(_ => buffer.Take()).ToList();

    [Fact]
    public void Buffer_waits_for_its_target_then_plays_in_order_and_stops_at_the_end()
    {
        var buffer = new JitterBuffer();
        buffer.Add(Packet(10, starts: true));
        Assert.Equal(PullKind.Idle, buffer.Take().Kind);
        buffer.Add(Packet(13));
        var pulls = new List<Pull> { buffer.Take() };
        buffer.Add(Packet(16, ends: true));
        pulls.AddRange(TakeAll(buffer, 9));
        Assert.Equal(Enumerable.Repeat(PullKind.Frame, 9).Append(PullKind.Idle), pulls.Select(p => p.Kind));
        Assert.Equal(Enumerable.Range(10, 9).Select(i => (byte)i), pulls.Take(9).Select(p => p.Data![0]));
        Assert.True(pulls[0].Restart);
        Assert.False(buffer.IsPlaying);
    }

    [Fact]
    public void A_short_word_plays_without_waiting_for_the_target()
    {
        var buffer = new JitterBuffer();
        buffer.Add(Packet(0, count: 2, starts: true, ends: true));
        Assert.Equal(new[] { PullKind.Frame, PullKind.Frame, PullKind.Idle }, TakeAll(buffer, 3).Select(p => p.Kind));
    }

    [Fact]
    public void A_burst_after_a_stall_is_trimmed_so_the_delay_does_not_build_up()
    {
        var buffer = new JitterBuffer();
        for (uint first = 0; first < 60; first += 3)
        {
            buffer.Add(Packet(first));
        }
        Pull pull = buffer.Take();
        Assert.Equal(PullKind.Frame, pull.Kind);
        // At most a packet more than the target is kept.
        Assert.Equal(60 - (JitterBuffer.StartTargetFrames + VoiceFormat.FramesPerPacket - 1), pull.Data![0]);
        Assert.True(buffer.DroppedFrames > 0);
    }

    [Fact]
    public void Late_frames_are_dropped_and_raise_the_target()
    {
        var buffer = new JitterBuffer();
        buffer.Add(Packet(0));
        buffer.Add(Packet(3));
        int target = buffer.TargetFrames;
        TakeAll(buffer, 3);
        buffer.Add(Packet(0));
        Assert.Equal(3, buffer.LateFrames);
        Assert.True(buffer.TargetFrames > target);
    }

    [Fact]
    public void A_gap_is_concealed_with_the_next_frame_for_error_correction()
    {
        var buffer = new JitterBuffer();
        buffer.Add(Packet(0));
        buffer.Add(Packet(3));
        Assert.Equal(new byte[] { 0, 1, 2 }, TakeAll(buffer, 3).Select(p => p.Data![0]));
        // Frames 6 to 8 never come; 9 to 11 do.
        buffer.Add(Packet(9));
        Assert.Equal(new byte[] { 3, 4, 5 }, TakeAll(buffer, 3).Select(p => p.Data![0]));
        List<Pull> gap = TakeAll(buffer, 3);
        Assert.All(gap, p => Assert.Equal(PullKind.Lost, p.Kind));
        Assert.Null(gap[0].Next);
        Assert.Null(gap[1].Next);
        Assert.Equal((byte)9, gap[2].Next![0]);
        Assert.Equal((byte)9, buffer.Take().Data![0]);
    }

    [Fact]
    public void Running_dry_conceals_briefly_then_keeps_time_then_ends()
    {
        var buffer = new JitterBuffer();
        buffer.Add(Packet(0));
        buffer.Add(Packet(3));
        TakeAll(buffer, 6);
        List<PullKind> kinds = TakeAll(buffer, JitterBuffer.UnderrunEndFrames + 1).Select(p => p.Kind).ToList();
        Assert.Equal(Enumerable.Repeat(PullKind.Lost, JitterBuffer.ConcealFrames), kinds.Take(JitterBuffer.ConcealFrames));
        Assert.Equal(PullKind.Silence, kinds[JitterBuffer.ConcealFrames]);
        Assert.Equal(PullKind.Idle, kinds[JitterBuffer.UnderrunEndFrames - 1]);
        Assert.False(buffer.IsPlaying);
    }

    [Fact]
    public void A_new_stretch_of_speech_starts_afresh_when_the_old_end_was_lost()
    {
        var buffer = new JitterBuffer();
        buffer.Add(Packet(0));
        buffer.Add(Packet(3));
        TakeAll(buffer, 6);
        buffer.Add(Packet(100, starts: true));
        buffer.Add(Packet(103));
        Pull pull = buffer.Take();
        Assert.Equal(PullKind.Frame, pull.Kind);
        Assert.True(pull.Restart);
        Assert.Equal((byte)100, pull.Data![0]);
    }

    [Fact]
    public void A_new_stream_replaces_the_old_one_and_a_flood_is_capped()
    {
        var buffer = new JitterBuffer();
        buffer.Add(Packet(0, stream: 1));
        buffer.Add(Packet(500, stream: 2));
        Assert.Equal(3, buffer.Buffered);
        for (uint first = 1000; first < 1300; first += 6)
        {
            buffer.Add(Packet(first, count: 6, stream: 2));
        }
        Assert.Equal(JitterBuffer.MaxFrames, buffer.Buffered);
    }

    // ---------- limiter ----------

    [Fact]
    public void Limiter_caps_peaks_and_turns_down_a_speaker_who_stays_too_loud()
    {
        var limiter = new VoiceLimiter();
        var square = new short[N];
        for (int frame = 0; frame < 300; frame++)
        {
            for (int i = 0; i < N; i++)
            {
                square[i] = (i / 20) % 2 == 0 ? short.MaxValue : short.MinValue;
            }
            limiter.Process(square, volume: 1.5f);
            Assert.True(square.Max(s => Math.Abs((int)s)) <= VoiceLimiter.Ceiling * 32768 + 1);
        }
        Assert.True(limiter.TooLoud);
        limiter.ResetLoudness();
        Assert.False(limiter.TooLoud);
    }

    [Fact]
    public void Limiter_leaves_normal_speech_alone()
    {
        var limiter = new VoiceLimiter();
        for (int f = 0; f < 300; f++)
        {
            short[] frame = Tone(f, -24);
            short[] copy = (short[])frame.Clone();
            limiter.Process(frame, volume: 1f);
            Assert.Equal(copy, frame);
        }
        Assert.False(limiter.TooLoud);
    }

    // ---------- mutes ----------

    [Fact]
    public void Mutes_follow_choices_then_the_strangers_setting()
    {
        var mutes = new VoiceMutes();
        int changes = 0;
        mutes.Changed += () => changes++;
        Assert.False(mutes.IsMuted(Bob, isFriend: false));
        mutes.StrangersStartMuted = true;
        Assert.True(mutes.IsMuted(Bob, isFriend: false));
        Assert.False(mutes.IsMuted(Carol, isFriend: true));
        mutes.Unmute(Bob);
        Assert.False(mutes.IsMuted(Bob, isFriend: false));
        mutes.Mute(Carol);
        Assert.True(mutes.IsMuted(Carol, isFriend: true));
        mutes.MuteAll(new[] { Bob, Carol });
        Assert.Equal(new[] { Bob, Carol }, mutes.Muted);
        Assert.Equal(3, changes);

        mutes.SetVolume(Bob, 5f);
        Assert.Equal(VoiceMutes.MaxVolume, mutes.VolumeOf(Bob));
        mutes.SetVolume(Bob, 1f);
        Assert.Empty(mutes.Volumes);

        var loaded = new VoiceMutes();
        loaded.Load(new[] { Carol }, new[] { Bob }, new[] { KeyValuePair.Create(Carol, 0.5f) });
        loaded.StrangersStartMuted = true;
        Assert.False(loaded.IsMuted(Bob, isFriend: false));
        Assert.True(loaded.IsMuted(Carol, isFriend: true));
        Assert.Equal(0.5f, loaded.VolumeOf(Carol));
    }

    // ---------- whole sessions ----------

    private sealed class Match
    {
        public readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        public readonly FakeDevice AliceDevice = new();
        public readonly FakeOutput AliceOutput = new();
        public readonly FakeOutput BobOutput = new();
        public readonly VoiceMutes BobMutes = new();
        public readonly VoiceSession AliceVoice;
        public readonly VoiceSession BobVoice;
        public readonly FakeLink AliceLink;
        public readonly FakeLink BobLink;
        public readonly VoiceCrypto BobCrypto;
        public readonly VoiceCrypto AliceCrypto;
        public long Now;

        public Match(FakeDevice? bobDevice = null, Action<string>? bobLog = null)
        {
            AliceVoice = new VoiceSession(Alice, AliceDevice, AliceOutput, new VoiceMutes(), _ => false, null, stream: 5);
            BobVoice = new VoiceSession(Bob, bobDevice, BobOutput, BobMutes, _ => false, bobLog, stream: 6);
            AliceLink = new FakeLink(Alice, Key) { DelayTicks = 2 };
            AliceLink.To.Add(BobVoice);
            AliceVoice.Link = AliceLink;
            BobLink = new FakeLink(Bob, Key);
            BobLink.To.Add(AliceVoice);
            BobVoice.Link = BobLink;
            AliceVoice.SetMembers(new[] { Alice, Bob });
            BobVoice.SetMembers(new[] { Alice, Bob });
            BobCrypto = new VoiceCrypto(Key);
            AliceCrypto = new VoiceCrypto(Key);
        }

        public void Run(int ticks, Func<int, short[]> sound, bool pushToTalk = false)
        {
            for (int t = 0; t < ticks; t++)
            {
                AliceDevice.Feed(sound(t));
                AliceVoice.Update(Now, pushToTalk);
                AliceLink.Deliver(BobCrypto);
                BobVoice.Update(Now, false);
                BobLink.Deliver(AliceCrypto);
                AliceOutput.Tick();
                BobOutput.Tick();
                Now += VoiceFormat.FrameMs;
            }
        }
    }

    [Fact]
    public void Two_players_talk_through_encryption_and_only_speech_is_sent()
    {
        var match = new Match();
        Assert.Equal(LocalVoiceState.Muted, match.AliceVoice.LocalState);
        match.Run(20, f => Tone(f, -30));
        Assert.Equal(0, match.AliceLink.Sent);

        match.AliceVoice.SetMic(true);
        match.Run(50, f => Tone(f, -70));
        int afterSilence = match.AliceLink.Sent;
        Assert.Equal(0, afterSilence);

        match.Run(100, f => Tone(f, -25));
        Assert.Equal(LocalVoiceState.Speaking, match.AliceVoice.LocalState);
        Assert.True(match.BobVoice.IsSpeaking(Alice));
        Assert.InRange(match.AliceLink.Sent, 30, 36);

        match.Run(60, f => Tone(f, -70));
        Assert.Equal(LocalVoiceState.Live, match.AliceVoice.LocalState);
        Assert.False(match.BobVoice.IsSpeaking(Alice));
        double heard = Db(match.BobOutput.Played.ToArray());
        _output.WriteLine($"Bob heard {match.BobOutput.Played.Count / (double)VoiceFormat.SampleRate:F2} s at {heard:F1} dB; {match.AliceLink.Sent} packets");
        Assert.True(match.BobOutput.Played.Count >= 100 * N);
        Assert.InRange(heard, -36, -24);
    }

    [Fact]
    public void The_match_summary_keeps_what_a_player_who_left_first_had_sent()
    {
        // A player who leaves the relay before the match ends must not take what was heard from it
        // out of the others' summary lines.
        var lines = new List<string>();
        var match = new Match(bobLog: lines.Add);
        match.AliceVoice.SetMic(true);
        match.Run(60, f => Tone(f, -25));
        match.Run(20, f => Tone(f, -70));
        int sent = match.AliceLink.Sent;
        Assert.True(sent > 0);

        match.BobVoice.SetMembers(new[] { Bob });
        Assert.False(match.BobVoice.IsSpeaking(Alice));
        match.BobVoice.Dispose();

        string summary = Assert.Single(lines, line => line.StartsWith("voice: "));
        Assert.Contains($"{Alice}: {sent} received, 0 muted", summary);
    }

    [Fact]
    public void A_muted_player_shows_as_speaking_but_is_never_played()
    {
        var match = new Match();
        match.BobVoice.Mute(Alice);
        match.AliceVoice.SetMic(true);
        match.Run(60, f => Tone(f, -25));
        Assert.True(match.BobVoice.IsSpeaking(Alice));
        Assert.True(match.BobVoice.IsMuted(Alice));
        Assert.Empty(match.BobOutput.Played);
        Assert.Equal(new[] { Alice }, match.BobMutes.Muted);
    }

    [Fact]
    public void Listening_off_plays_nothing()
    {
        var match = new Match();
        match.BobVoice.Hearing = false;
        match.AliceVoice.SetMic(true);
        match.Run(60, f => Tone(f, -25));
        Assert.Empty(match.BobOutput.Played);
    }

    [Fact]
    public void Push_to_talk_sends_only_while_held_even_in_silence()
    {
        var match = new Match();
        match.AliceVoice.PushToTalkOnly = true;
        match.AliceVoice.SetMic(true);
        match.Run(30, f => Tone(f, -25));
        Assert.Equal(0, match.AliceLink.Sent);
        match.Run(30, f => Tone(f, -80), pushToTalk: true);
        Assert.InRange(match.AliceLink.Sent, 9, 11);
        match.Run(10, f => Tone(f, -80));
        Assert.False(match.AliceDevice.Capturing);
    }

    [Fact]
    public void The_first_mic_use_asks_for_permission_and_a_refusal_means_listening_only()
    {
        var match = new Match();
        match.AliceDevice.CaptureState = VoiceCaptureState.NeedsPermission;
        match.AliceVoice.SetMic(true);
        Assert.Equal(1, match.AliceDevice.PermissionRequests);
        Assert.Equal(LocalVoiceState.Asking, match.AliceVoice.LocalState);
        match.AliceDevice.CaptureState = VoiceCaptureState.Ready;
        match.Run(1, f => Tone(f, -70));
        Assert.True(match.AliceVoice.MicOn);

        var refused = new Match();
        refused.AliceDevice.CaptureState = VoiceCaptureState.NeedsPermission;
        refused.AliceVoice.SetMic(true);
        refused.AliceDevice.CaptureState = VoiceCaptureState.Denied;
        refused.Run(1, f => Tone(f, -70));
        Assert.False(refused.AliceVoice.MicOn);
        Assert.Equal(LocalVoiceState.ListenOnly, refused.AliceVoice.LocalState);
    }

    [Fact]
    public void States_for_no_device_no_link_and_voice_off()
    {
        var match = new Match();
        Assert.Equal(LocalVoiceState.ListenOnly, match.BobVoice.LocalState);
        ((FakeLink)match.BobVoice.Link!).IsUp = false;
        Assert.Equal(LocalVoiceState.Unavailable, match.BobVoice.LocalState);
        match.BobVoice.Enabled = false;
        Assert.Equal(LocalVoiceState.Off, match.BobVoice.LocalState);

        match.AliceVoice.SetMic(true);
        match.AliceLink.IsUp = false;
        match.Run(30, f => Tone(f, -25));
        Assert.Equal(0, match.AliceLink.Sent);
        Assert.False(match.AliceDevice.Capturing);
    }

    [Fact]
    public void Each_player_sees_what_the_others_voice_is_set_to()
    {
        var match = new Match();
        match.Run(5, f => Tone(f, -70));
        Assert.Equal(VoiceStatus.Muted, match.BobVoice.StatusOf(Alice));
        // Bob has no microphone (as on Android): listening only.
        Assert.Equal(VoiceStatus.ListenOnly, match.AliceVoice.StatusOf(Bob));

        match.AliceVoice.SetMic(true);
        match.Run(5, f => Tone(f, -70));
        Assert.Equal(VoiceStatus.Live, match.BobVoice.StatusOf(Alice));
        int statusPackets = match.AliceLink.StatusSent;

        // Repeated only every few seconds while nothing changes.
        match.Run((int)(VoiceSession.StatusEveryMs / VoiceFormat.FrameMs), f => Tone(f, -70));
        Assert.Equal(statusPackets + 1, match.AliceLink.StatusSent);

        match.AliceVoice.Enabled = false;
        match.Run(5, f => Tone(f, -70));
        Assert.Equal(VoiceStatus.Off, match.BobVoice.StatusOf(Alice));
    }

    [Fact]
    public void Packets_from_outside_the_match_and_floods_are_dropped()
    {
        var match = new Match();
        match.BobVoice.Update(0, false);
        match.BobVoice.Receive(Carol, Packet(0, starts: true));
        Assert.False(match.BobVoice.IsSpeaking(Carol));
        Assert.Null(match.BobVoice.StatusOf(Carol));
        for (uint i = 0; i < 200; i++)
        {
            match.BobVoice.Receive(Alice, Packet(i * 3));
        }
        // Only the first 60 of the second were taken: at most 50 frames wait.
        match.BobVoice.Update(0, false);
        Assert.True(match.BobOutput.Played.Count <= VoiceSession.OutputQueueSamples);
    }
}
