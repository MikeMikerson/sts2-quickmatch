using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace QuickMatch.Voice;

/// <summary>How voice packets reach the other players of the match.</summary>
internal interface IVoiceLink
{
    /// <summary>True while packets can reach the others: the relay room is joined, or the game's Steam connection is up.</summary>
    bool IsUp { get; }

    void Send(VoicePacket packet);
}

/// <summary>What the local player's mic button shows.</summary>
internal enum LocalVoiceState
{
    /// <summary>Voice chat is switched off in the settings.</summary>
    Off,
    /// <summary>No fast link to the others (relay down or used up): voice cannot travel.</summary>
    Unavailable,
    /// <summary>This device cannot record, or the player refused the microphone: listening only.</summary>
    ListenOnly,
    /// <summary>The system's microphone prompt is showing.</summary>
    Asking,
    Muted,
    /// <summary>Mic on, nothing being sent (silence, or push to talk not held).</summary>
    Live,
    Speaking,
}

/// <summary>
/// Voice chat for one match, on the game's thread: records the local player when they choose to
/// talk, sends only speech (<see cref="VoiceGate"/>), and mixes what the others say through a jitter
/// buffer, a decoder and a limiter for each of them. Muted players are dropped before anything is
/// decoded. Knows nothing of Godot or the network: the platform's device, the output and the link
/// are handed in, so this whole class runs in the offline tests.
/// </summary>
internal sealed class VoiceSession : IDisposable
{
    /// <summary>Voice kept queued at the output (40 ms): enough to ride out an uneven frame rate.</summary>
    public const int OutputQueueSamples = 2 * VoiceFormat.FrameSamples;
    /// <summary>A player counts as speaking for this long after their last packet.</summary>
    public const long SpeakingHoldMs = 300;
    /// <summary>Packets accepted from one player in a second (they send about 17); the rest are dropped.</summary>
    public const int MaxPacketsPerSecond = 60;
    /// <summary>The local status is sent when it changes and at least this often, for players who join later.</summary>
    public const long StatusEveryMs = 5000;
    /// <summary>Frames mixed per update at most, so a stalled game frame never turns into a burst of work.</summary>
    private const int MaxFramesPerUpdate = 8;

    private sealed class Peer
    {
        public readonly JitterBuffer Buffer = new();
        public readonly VoiceDecoder Decoder = new();
        public readonly VoiceLimiter Limiter = new();
        public long SpeakingUntil = long.MinValue;
        public VoiceStatus? Status;
        public long WindowStart;
        public int WindowPackets;
        public int Received;
        public int Dropped;
    }

    private readonly ulong _local;
    private readonly IVoiceDevice? _device;
    private readonly IVoiceOutput _output;
    private readonly VoiceMutes _mutes;
    private readonly Func<ulong, bool> _isFriend;
    private readonly Action<string>? _log;
    private readonly uint _stream;
    private readonly HashSet<ulong> _members = new();
    private readonly Dictionary<ulong, Peer> _peers = new();
    /// <summary>What each player sent this match, kept for the summary line after they leave the relay.</summary>
    private readonly Dictionary<ulong, (int Received, int Dropped, int Late, int Concealed)> _tallies = new();
    private readonly VoiceGate _gate = new();
    private readonly VoiceEncoder _encoder = new();
    private readonly short[] _captured = new short[VoiceFormat.FrameSamples * 16];
    private int _capturedCount;
    private readonly short[] _previous = new short[VoiceFormat.FrameSamples];
    private bool _hasPrevious;
    private readonly List<byte[]> _outgoing = new();
    private uint _outgoingFirst;
    private uint _frameClock;
    private bool _capturing;
    private bool _sending;
    private bool _startsTalk;
    private bool _wantMic;
    private readonly short[] _pcm = new short[VoiceFormat.FrameSamples];
    private readonly int[] _mix = new int[VoiceFormat.FrameSamples];
    private readonly short[] _mixed = new short[VoiceFormat.FrameSamples];
    private long _now;
    private bool _outputActive;
    private int _packetsSent;
    private int _packetsTooMany;
    private uint _statusSeq;
    private VoiceStatus? _statusSent;
    private long _statusSentAt;

    public VoiceSession(ulong local, IVoiceDevice? device, IVoiceOutput output, VoiceMutes mutes, Func<ulong, bool> isFriend,
        Action<string>? log, uint? stream = null)
    {
        _local = local;
        _device = device;
        _output = output;
        _mutes = mutes;
        _isFriend = isFriend;
        _log = log;
        _stream = stream ?? BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    }

    public IVoiceLink? Link { get; set; }

    /// <summary>Voice chat on in the settings. Off: nothing is recorded, sent or played.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>"Hear voice": off drops everything the others send.</summary>
    public bool Hearing { get; set; } = true;

    /// <summary>The mic button: starts muted in every match.</summary>
    public bool MicOn { get; private set; }

    /// <summary>Push to talk instead of voice activation: only a held key sends.</summary>
    public bool PushToTalkOnly { get; set; }

    /// <summary>The master voice volume, 0 to 1.</summary>
    public float Volume { get; set; } = 1f;

    public double Sensitivity
    {
        get => _gate.Sensitivity;
        set => _gate.Sensitivity = value;
    }

    /// <summary>The local mic level from 0 to 1 while recording, for the meter.</summary>
    public double MicLevel => _capturing ? _gate.Meter : 0;

    public bool CanCapture => _device != null && _device.CaptureState is not (VoiceCaptureState.Unsupported or VoiceCaptureState.Denied);

    public LocalVoiceState LocalState
    {
        get
        {
            if (!Enabled)
            {
                return LocalVoiceState.Off;
            }
            if (Link is not { IsUp: true })
            {
                return LocalVoiceState.Unavailable;
            }
            if (!CanCapture)
            {
                return LocalVoiceState.ListenOnly;
            }
            if (_device!.CaptureState == VoiceCaptureState.Asking || (_wantMic && !MicOn))
            {
                return LocalVoiceState.Asking;
            }
            if (_sending)
            {
                return LocalVoiceState.Speaking;
            }
            return MicOn || PushToTalkOnly ? LocalVoiceState.Live : LocalVoiceState.Muted;
        }
    }

    /// <summary>The other players of the match. Packets from anyone else are dropped.</summary>
    public IReadOnlyCollection<ulong> Members => _members;

    public void SetMembers(IEnumerable<ulong> members)
    {
        _members.Clear();
        foreach (ulong member in members)
        {
            if (member != _local)
            {
                _members.Add(member);
            }
        }
        foreach (ulong gone in _peers.Keys.Where(id => !_members.Contains(id)).ToList())
        {
            KeepTally(gone, _peers[gone]);
            _peers.Remove(gone);
        }
    }

    /// <summary>Turns the mic on or off. The first time, this asks the system for permission.</summary>
    public void SetMic(bool on)
    {
        if (!on)
        {
            MicOn = false;
            _wantMic = false;
            return;
        }
        if (_device == null)
        {
            return;
        }
        switch (_device.CaptureState)
        {
            case VoiceCaptureState.Ready:
                MicOn = true;
                break;
            case VoiceCaptureState.NeedsPermission:
                _wantMic = true;
                _device.RequestPermission();
                break;
            case VoiceCaptureState.Asking:
                _wantMic = true;
                break;
        }
    }

    public void ToggleMic() => SetMic(!(MicOn || _wantMic));

    public bool IsSpeaking(ulong player) => _peers.TryGetValue(player, out Peer? peer) && _now < peer.SpeakingUntil;

    public bool IsMuted(ulong player) => _mutes.IsMuted(player, _isFriend(player));

    public bool IsTooLoud(ulong player) => _peers.TryGetValue(player, out Peer? peer) && peer.Limiter.TooLoud;

    /// <summary>What another player last said their voice is set to, or null if they have not said.</summary>
    public VoiceStatus? StatusOf(ulong player) => _peers.TryGetValue(player, out Peer? peer) ? peer.Status : null;

    /// <summary>What the local player's voice is set to, as the others see it.</summary>
    public VoiceStatus LocalStatus => LocalState switch
    {
        LocalVoiceState.Off => VoiceStatus.Off,
        LocalVoiceState.ListenOnly => VoiceStatus.ListenOnly,
        LocalVoiceState.Live or LocalVoiceState.Speaking => VoiceStatus.Live,
        _ => _device == null || !CanCapture ? VoiceStatus.ListenOnly : VoiceStatus.Muted,
    };

    public void Mute(ulong player)
    {
        _mutes.Mute(player);
        if (_peers.TryGetValue(player, out Peer? peer))
        {
            peer.Buffer.Clear();
        }
    }

    public void Unmute(ulong player) => _mutes.Unmute(player);

    /// <summary>"Mute everyone else": every other player of this match.</summary>
    public void MuteEveryone()
    {
        _mutes.MuteAll(_members);
        foreach (Peer peer in _peers.Values)
        {
            peer.Buffer.Clear();
        }
    }

    public float VolumeOf(ulong player) => _mutes.VolumeOf(player);

    /// <summary>Sets a player's volume, which also undoes their automatic turning down.</summary>
    public void SetVolume(ulong player, float volume)
    {
        _mutes.SetVolume(player, volume);
        if (_peers.TryGetValue(player, out Peer? peer))
        {
            peer.Limiter.ResetLoudness();
        }
    }

    /// <summary>A packet from another player, already decrypted where it came over the relay.</summary>
    public void Receive(ulong sender, VoicePacket packet)
    {
        if (sender == _local || !_members.Contains(sender))
        {
            return;
        }
        if (!_peers.TryGetValue(sender, out Peer? peer))
        {
            peer = new Peer { WindowStart = _now };
            _peers[sender] = peer;
        }
        if (_now - peer.WindowStart >= 1000)
        {
            peer.WindowStart = _now;
            peer.WindowPackets = 0;
        }
        if (++peer.WindowPackets > MaxPacketsPerSecond)
        {
            _packetsTooMany++;
            return;
        }
        if (packet.Status is { } status)
        {
            peer.Status = status;
            return;
        }
        peer.Received++;
        // The ring shows who is talking even when they are muted, so the listener knows whom they muted.
        peer.SpeakingUntil = packet.EndsTalk ? _now + SpeakingHoldMs / 3 : _now + SpeakingHoldMs;
        if (!Enabled || !Hearing || IsMuted(sender))
        {
            peer.Dropped++;
            return;
        }
        peer.Buffer.Add(packet);
    }

    /// <summary>Runs once per game frame: records and sends, then plays what has arrived.</summary>
    public void Update(long now, bool pushToTalkHeld)
    {
        _now = now;
        if (_wantMic && _device != null)
        {
            switch (_device.CaptureState)
            {
                case VoiceCaptureState.Ready:
                    _wantMic = false;
                    MicOn = true;
                    break;
                case VoiceCaptureState.Denied or VoiceCaptureState.Unsupported:
                    _wantMic = false;
                    break;
            }
        }
        if (pushToTalkHeld && !MicOn && _device?.CaptureState == VoiceCaptureState.NeedsPermission)
        {
            _device.RequestPermission();
        }
        UpdateCapture(pushToTalkHeld);
        UpdatePlayback();
        UpdateStatus();
    }

    public void Dispose()
    {
        StopCapture();
        if (_outputActive)
        {
            _output.Stop();
            _outputActive = false;
        }
        foreach ((ulong id, Peer peer) in _peers)
        {
            KeepTally(id, peer);
        }
        if (_packetsSent > 0 || _tallies.Count > 0)
        {
            string heard = string.Join(", ", _tallies.Select(t => $"{t.Key}: {t.Value.Received} received, {t.Value.Dropped} muted, " +
                $"{t.Value.Late} late, {t.Value.Concealed} concealed"));
            _log?.Invoke($"voice: {_packetsSent} packets sent; {heard}; {_packetsTooMany} over the rate limit");
        }
        _peers.Clear();
        _tallies.Clear();
    }

    // A player who leaves and comes back in the same match gets a new peer; their counts add up.
    private void KeepTally(ulong id, Peer peer)
    {
        _tallies.TryGetValue(id, out var tally);
        _tallies[id] = (tally.Received + peer.Received, tally.Dropped + peer.Dropped,
            tally.Late + peer.Buffer.LateFrames, tally.Concealed + peer.Buffer.ConcealedFrames);
    }

    private void UpdateCapture(bool pushToTalkHeld)
    {
        bool allowed = Enabled && Link is { IsUp: true } && _device?.CaptureState == VoiceCaptureState.Ready;
        bool talk = allowed && (pushToTalkHeld || (MicOn && !PushToTalkOnly));
        if (!talk)
        {
            StopCapture();
            return;
        }
        if (!_capturing)
        {
            if (!_device!.StartCapture())
            {
                return;
            }
            _capturing = true;
            _capturedCount = 0;
            _hasPrevious = false;
            _gate.Reset();
        }
        while (true)
        {
            int read = _device!.ReadCapture(_captured.AsSpan(_capturedCount));
            _capturedCount += read;
            int at = 0;
            while (_capturedCount - at >= VoiceFormat.FrameSamples)
            {
                CaptureFrame(_captured.AsSpan(at, VoiceFormat.FrameSamples), pushToTalkHeld);
                at += VoiceFormat.FrameSamples;
            }
            _captured.AsSpan(at, _capturedCount - at).CopyTo(_captured);
            _capturedCount -= at;
            if (read == 0 || _capturedCount == _captured.Length)
            {
                break;
            }
        }
    }

    private void CaptureFrame(ReadOnlySpan<short> frame, bool pushToTalkHeld)
    {
        uint index = _frameClock++;
        bool open = _gate.Process(frame);
        bool send = pushToTalkHeld || (!PushToTalkOnly && open);
        if (send)
        {
            if (!_sending)
            {
                _sending = true;
                _startsTalk = true;
                _encoder.Reset();
                // The frame just before speech was detected carries the start of the first word.
                if (_hasPrevious && index > 0)
                {
                    Encode(index - 1, _previous);
                }
            }
            Encode(index, frame);
        }
        else if (_sending)
        {
            _sending = false;
            Flush(endsTalk: true);
        }
        frame.CopyTo(_previous);
        _hasPrevious = true;
    }

    private void Encode(uint index, ReadOnlySpan<short> frame)
    {
        if (_outgoing.Count == 0)
        {
            _outgoingFirst = index;
        }
        _outgoing.Add(_encoder.Encode(frame));
        if (_outgoing.Count == VoiceFormat.FramesPerPacket)
        {
            Flush(endsTalk: false);
        }
    }

    private void Flush(bool endsTalk)
    {
        if (_outgoing.Count == 0)
        {
            return;
        }
        var packet = new VoicePacket(_stream, _outgoingFirst, _startsTalk, endsTalk, _outgoing.ToArray());
        _outgoing.Clear();
        _startsTalk = false;
        if (Link is { IsUp: true } link)
        {
            link.Send(packet);
            _packetsSent++;
        }
    }

    private void StopCapture()
    {
        if (!_capturing)
        {
            return;
        }
        if (_sending)
        {
            _sending = false;
            Flush(endsTalk: true);
        }
        _outgoing.Clear();
        _device?.StopCapture();
        _capturing = false;
        _capturedCount = 0;
        _gate.Reset();
    }

    private void UpdateStatus()
    {
        if (Link is not { IsUp: true } link)
        {
            _statusSent = null;
            return;
        }
        VoiceStatus status = LocalStatus;
        if (status == _statusSent && _now - _statusSentAt < StatusEveryMs)
        {
            return;
        }
        // Under the inverted stream number, so no status nonce ever equals an audio one.
        link.Send(VoicePacket.ForStatus(~_stream, _statusSeq++, status));
        _statusSent = status;
        _statusSentAt = _now;
    }

    private void UpdatePlayback()
    {
        if (!Enabled || !Hearing)
        {
            foreach (Peer peer in _peers.Values)
            {
                peer.Buffer.Clear();
            }
            if (_outputActive)
            {
                _output.Stop();
                _outputActive = false;
            }
            return;
        }
        for (int frames = 0; frames < MaxFramesPerUpdate && _output.QueuedSamples < OutputQueueSamples; frames++)
        {
            Array.Clear(_mix);
            bool any = false;
            foreach ((ulong id, Peer peer) in _peers)
            {
                Pull pull = peer.Buffer.Take();
                switch (pull.Kind)
                {
                    case PullKind.Idle:
                        continue;
                    case PullKind.Frame:
                        if (pull.Restart)
                        {
                            peer.Decoder.Reset();
                        }
                        peer.Decoder.Decode(pull.Data, _pcm);
                        break;
                    case PullKind.Lost:
                        peer.Decoder.Conceal(_pcm, pull.Next);
                        break;
                    case PullKind.Silence:
                        Array.Clear(_pcm);
                        break;
                }
                any = true;
                peer.Limiter.Process(_pcm, _mutes.VolumeOf(id));
                for (int i = 0; i < _mix.Length; i++)
                {
                    _mix[i] += _pcm[i];
                }
            }
            if (!any)
            {
                break;
            }
            float volume = Math.Clamp(Volume, 0f, 1f);
            for (int i = 0; i < _mix.Length; i++)
            {
                _mixed[i] = (short)Math.Clamp((int)(_mix[i] * volume), short.MinValue, short.MaxValue);
            }
            _output.Play(_mixed);
            _outputActive = true;
        }
    }
}
