using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMatch.Voice;

internal enum PullKind
{
    /// <summary>Not speaking: nothing to play.</summary>
    Idle,
    /// <summary>A frame to decode and play.</summary>
    Frame,
    /// <summary>A frame that has not arrived: conceal it (from the next frame's error correction if given).</summary>
    Lost,
    /// <summary>Still speaking but nothing has arrived for a while: play silence to keep time.</summary>
    Silence,
}

/// <param name="Restart">The first frame of a new stretch of speech: the decoder should forget the last one.</param>
internal readonly record struct Pull(PullKind Kind, byte[]? Data = null, byte[]? Next = null, bool Restart = false);

/// <summary>
/// Puts one speaker's packets back in order and in time. Packets come in bursts (the relay is one
/// WebSocket, so a slow moment holds everything back and then delivers it at once), so playing
/// starts only once <see cref="TargetFrames"/> are waiting (keeping at most a packet more), and
/// while playing anything more than the target plus <see cref="CatchUpSlack"/> is dropped from the
/// oldest end, so the delay never builds up. The
/// target grows by a frame when a frame comes too late or the buffer runs dry, and shrinks again
/// after a calm stretch. A packet marked as the end of speech lets the buffer stop cleanly; one that
/// never comes ends speech after <see cref="UnderrunEndFrames"/> without frames, or as soon as the
/// next stretch of speech arrives.
///
/// <see cref="Take"/> is called once per 20 ms frame of playback, which is the buffer's only clock.
/// </summary>
internal sealed class JitterBuffer
{
    public const int MinTargetFrames = 4;
    public const int StartTargetFrames = 5;
    public const int MaxTargetFrames = 10;
    /// <summary>Frames beyond the target that are allowed before the oldest are dropped.</summary>
    public const int CatchUpSlack = 6;
    /// <summary>The most frames held at all (one second): a flooding sender cannot make more wait.</summary>
    public const int MaxFrames = 50;
    /// <summary>Missing frames concealed before playing plain silence; a longer gap is a pause.</summary>
    public const int ConcealFrames = 3;
    /// <summary>Frames without anything arriving before the speech is taken as over (200 ms).</summary>
    public const int UnderrunEndFrames = 10;
    /// <summary>Calm frames played before the target shrinks by one (10 s).</summary>
    public const int CalmFramesToShrink = 500;

    private readonly SortedDictionary<uint, (byte[] Data, bool Starts, bool Ends)> _frames = new();
    private uint _stream;
    private bool _hasStream;
    private uint _next;
    private int _underrun;
    private int _calm;

    public int TargetFrames { get; private set; } = StartTargetFrames;

    /// <summary>True while playing a stretch of speech.</summary>
    public bool IsPlaying { get; private set; }

    public int Buffered => _frames.Count;

    public int LateFrames { get; private set; }

    public int DroppedFrames { get; private set; }

    public int ConcealedFrames { get; private set; }

    public void Add(VoicePacket packet)
    {
        if (!_hasStream || packet.Stream != _stream)
        {
            // A new voice session of this speaker (their app restarted, or voice was switched off and on).
            _frames.Clear();
            IsPlaying = false;
            _stream = packet.Stream;
            _hasStream = true;
        }
        int last = packet.Frames.Count - 1;
        for (int i = 0; i <= last; i++)
        {
            uint index = packet.FirstFrame + (uint)i;
            if (IsPlaying && index < _next)
            {
                LateFrames++;
                Grow();
                continue;
            }
            _frames.TryAdd(index, (packet.Frames[i], packet.StartsTalk && i == 0, packet.EndsTalk && i == last));
        }
        while (_frames.Count > MaxFrames)
        {
            _frames.Remove(_frames.Keys.First());
            DroppedFrames++;
        }
    }

    /// <summary>What to play for the next 20 ms.</summary>
    public Pull Take()
    {
        bool restart = false;
        if (!IsPlaying)
        {
            if (_frames.Count == 0 || (_frames.Count < TargetFrames && !_frames.Values.Any(f => f.Ends)))
            {
                return new Pull(PullKind.Idle);
            }
            // Frames arrive a packet at a time, so up to a packet more than the target is normal;
            // anything beyond that is a burst after a stall and is dropped.
            CatchUp(TargetFrames + VoiceFormat.FramesPerPacket - 1);
            IsPlaying = true;
            restart = true;
            _next = _frames.Keys.First();
            _underrun = 0;
        }
        else if (_frames.Count > TargetFrames + CatchUpSlack)
        {
            CatchUp(TargetFrames);
            _next = Math.Max(_next, _frames.Keys.First());
        }

        if (_frames.Remove(_next, out var frame))
        {
            _next++;
            _underrun = 0;
            if (frame.Ends)
            {
                IsPlaying = false;
            }
            if (++_calm >= CalmFramesToShrink && TargetFrames > MinTargetFrames)
            {
                TargetFrames--;
                _calm = 0;
            }
            return new Pull(PullKind.Frame, frame.Data, Restart: restart);
        }

        if (_frames.Count > 0)
        {
            uint first = _frames.Keys.First();
            if (first - _next > ConcealFrames || _frames[first].Starts)
            {
                // The end of the last stretch never came and the next one is here: start it afresh.
                IsPlaying = false;
                return Take();
            }
            // A short gap with later frames waiting: conceal it, from the next frame's error
            // correction if that is the one right after.
            _next++;
            ConcealedFrames++;
            Grow();
            _frames.TryGetValue(_next, out var after);
            return new Pull(PullKind.Lost, Next: after.Data);
        }

        _next++;
        if (++_underrun == 1)
        {
            Grow();
        }
        if (_underrun >= UnderrunEndFrames)
        {
            IsPlaying = false;
            return new Pull(PullKind.Idle);
        }
        if (_underrun <= ConcealFrames)
        {
            ConcealedFrames++;
            return new Pull(PullKind.Lost);
        }
        return new Pull(PullKind.Silence);
    }

    /// <summary>Drops everything waiting and stops playing (the speaker was muted, or listening was switched off).</summary>
    public void Clear()
    {
        _frames.Clear();
        IsPlaying = false;
    }

    /// <summary>Drops the oldest frames until at most <paramref name="keep"/> are waiting.</summary>
    private void CatchUp(int keep)
    {
        while (_frames.Count > keep)
        {
            _frames.Remove(_frames.Keys.First());
            DroppedFrames++;
        }
    }

    private void Grow()
    {
        _calm = 0;
        if (TargetFrames < MaxTargetFrames)
        {
            TargetFrames++;
        }
    }
}
