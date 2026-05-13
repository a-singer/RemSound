using System.Net;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Multi-source playout coordinator. Holds one <see cref="SessionPlayout"/> per active sender and
/// implements <see cref="IWaveProvider"/> by reading from all of them per WASAPI render callback
/// and summing into the output buffer. Volume / mute / clipping live here; per-session adaptive
/// rate lives inside each SessionPlayout.
///
/// Why multi-source: the previous design supported only one sender at a time and reset the
/// playout buffer on every endpoint change. With two senders simultaneously sending to the same
/// receiver (e.g. a peer and your own loopback for monitoring), Format packets arrived alternately
/// from each endpoint and the buffer was flushed several times per second — horrible crackle.
/// Now each sender owns its own buffer + drift corrector, and the mix bus sums them.
///
/// Concurrent-modification safety: <see cref="GetOrCreateSession"/> / <see cref="RemoveSession"/>
/// take a lock and mutate the dictionary; <see cref="Read"/> snapshots the current values list
/// (no allocation in steady state once the snapshot array has stabilised) before iterating, so it
/// never iterates a mid-mutation collection. The per-session Read is lock-free.
/// </summary>
internal sealed class PlayoutEngine : IWaveProvider
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MixBytesPerFrame = MixChannels * sizeof(float);
    private const int MixBytesPerSecond = MixSampleRate * MixBytesPerFrame;

    // Soft-limiter parameters. Below the threshold, samples pass through untouched. Above it,
    // a tanh-based soft-knee smoothly compresses excess so the output asymptotes to ±1 without
    // ever clipping hard. This replaces the previous straight `Math.Clamp(-1, +1)` which slams
    // peaks into a square wave on transient summation. Standard pattern in audio mixers — see
    // research notes on conferencing-mixer clipping (NetEQ uses similar; PJSIP / RTP mixers too).
    private const float LimiterThreshold = 0.9f;
    private const float LimiterKnee = 1.0f - LimiterThreshold;

    private readonly ReceiverDiagnostics diagnostics;
    private readonly object sessionsLock = new();
    // Sessions are keyed by (Endpoint, StreamId) — 2026-05-11. One peer can produce
    // multiple simultaneous streams (e.g. WASAPI lane + ASIO lane in the native-
    // independent audio mode). For the existing single-lane modes (WasapiOnly / AsioOnly /
    // Both) the sender emits a single streamId so the dict still has one entry per peer,
    // identical to the pre-refactor behaviour. The new mode adds a second entry per peer.
    private readonly Dictionary<(IPEndPoint Endpoint, ushort StreamId), SessionPlayout> sessions = new();
    private SessionPlayout[] sessionsSnapshot = [];
    // Per-route scratch. Each IWaveProvider surface (Mixed / WasapiLane / AsioLane) runs on
    // its own consumer thread in BothIndependent mode (WASAPI master producer + ASIO render
    // thread, independent). They must not share scratch arrays — concurrent writes would
    // garble output. The Mixed-route scratch keeps the original field names because that's
    // what the legacy Read still uses; the lane-route surfaces own their own copies.
    private float[] mixScratch = new float[8192];
    private float[] sessionScratch = new float[8192];
    private readonly LaneOutput wasapiLaneOutput;
    private readonly LaneOutput asioLaneOutput;

    // Per-route latency state. Stage 4.5 (2026-05-11): added so BothIndependent mode can run
    // each lane at its own target/max without one lane's auto-tune dragging the other up.
    // In classic modes only the Mixed route is ever read from; the others sit at defaults
    // and consume no resources. Each LaneLatency's fields are volatile so UI-thread writes
    // are visible to the audio render thread without locks.
    private sealed class LaneLatency
    {
        public volatile int TargetMs = 30;
        public volatile int MaxMs = 80;
    }
    private readonly LaneLatency mixedLatency = new();
    private readonly LaneLatency wasapiLaneLatency = new();
    private readonly LaneLatency asioLaneLatency = new();
    private volatile bool muted;
    private volatile float volume = 1f;
    // 1 = stupid aggressive, 10 = perfectly smooth. Read on the audio thread, written from UI.
    // Now mostly a safety-knob for the click-trim catastrophic path; in normal operation the
    // Phase-2 drift corrector (in SessionPlayout) keeps the buffer near target so the trim
    // never fires regardless of this value.
    private volatile int smoothness = 3;
    // User-pickable artifact for underrun gaps. Stored as raw int because volatile doesn't
    // play with enum types directly. Push to existing SessionPlayouts on change so already-
    // running streams pick the new artifact up on the very next gap.
    private volatile int concealmentArtifactRaw = (int)ConcealmentArtifact.NoiseBurst;

    public void SetSmoothness(int value) => smoothness = Math.Clamp(value, 1, 10);

    /// <summary>Sets the concealment artifact for every active session and for any future
    /// session created after this call. Live-updates: the next time a session sees an
    /// underrun, it uses the new artifact.</summary>
    public void SetConcealmentArtifact(ConcealmentArtifact artifact)
    {
        concealmentArtifactRaw = (int)artifact;
        var snap = sessionsSnapshot;
        foreach (var s in snap) s.SetConcealmentArtifact(artifact);
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels);

    /// <summary>Legacy property returning the Mixed route's target. Used by code paths that
    /// don't care about per-route routing (every classic mode, plus diagnostics that report
    /// "the" target latency in non-BothIndependent setups).</summary>
    public int TargetLatencyMs => mixedLatency.TargetMs;
    /// <summary>Legacy property returning the Mixed route's max.</summary>
    public int MaxLatencyMs => mixedLatency.MaxMs;

    /// <summary>Per-route target accessor. In BothIndependent the WASAPI and ASIO routes have
    /// independent targets so each lane can settle at its native latency without the other
    /// pulling it. In classic modes only Mixed is meaningful; the other two routes return
    /// their defaults.</summary>
    public int TargetLatencyMsFor(RenderRoute route) => LatencyFor(route).TargetMs;
    public int MaxLatencyMsFor(RenderRoute route) => LatencyFor(route).MaxMs;

    private LaneLatency LatencyFor(RenderRoute route) => route switch
    {
        RenderRoute.WasapiLane => wasapiLaneLatency,
        RenderRoute.AsioLane => asioLaneLatency,
        _ => mixedLatency,
    };

    /// <summary>Aggregate buffered ms across all active sessions. Used by the App's diagnostic
    /// snapshot row. Per-session levels are not currently exposed (single number is enough for
    /// the existing snapshot column; the auto-tune doesn't depend on it).</summary>
    public int CurrentBufferMs
    {
        get
        {
            var snap = sessionsSnapshot;
            if (snap.Length == 0) return 0;
            var totalBytes = 0;
            foreach (var s in snap) totalBytes += s.BufferedBytes;
            return totalBytes / MixBytesPerFrame * 1000 / MixSampleRate;
        }
    }

    public bool IsArmed
    {
        get
        {
            var snap = sessionsSnapshot;
            foreach (var s in snap) if (s.IsArmed) return true;
            return false;
        }
    }

    public float Volume
    {
        get => volume;
        set => volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsMuted
    {
        get => muted;
        set => muted = value;
    }

    public PlayoutEngine(ReceiverDiagnostics diagnostics)
    {
        this.diagnostics = diagnostics;
        wasapiLaneOutput = new LaneOutput(this, RenderRoute.WasapiLane);
        asioLaneOutput = new LaneOutput(this, RenderRoute.AsioLane);
    }

    /// <summary>
    /// IWaveProvider surface for sessions tagged <see cref="RenderRoute.WasapiLane"/>. Only
    /// used in BothIndependent mode where the WASAPI render backend reads its own lane
    /// independently of the ASIO render. In the three classic modes (WasapiOnly / AsioOnly /
    /// Both) nothing ever reads from this surface and no session is ever tagged WasapiLane,
    /// so it returns silence and consumes no resources.
    /// </summary>
    public IWaveProvider WasapiLaneOutput => wasapiLaneOutput;

    /// <summary>
    /// IWaveProvider surface for sessions tagged <see cref="RenderRoute.AsioLane"/>. Same
    /// contract as <see cref="WasapiLaneOutput"/>; only used in BothIndependent mode.
    /// </summary>
    public IWaveProvider AsioLaneOutput => asioLaneOutput;

    /// <summary>
    /// Sets the user's delay knob. Slider value drives the playout target directly so the change
    /// is audible immediately.
    ///
    /// LOWER: by default disarms + drains every session. The buffer is now above the new target
    /// and has to actually shrink before playback resumes. Brief silence is unavoidable on this
    /// path; the user is asking for tighter latency and accepting the cost. Set
    /// <paramref name="drainOnLower"/> = false to take the SOFT path instead — the buffer keeps
    /// playing and the drift corrector's adaptive gain ramps it down over a few seconds. Used
    /// for auto-tune-driven lowers, where the user didn't ask for an immediate change and
    /// shouldn't hear one.
    ///
    /// RAISE (2026-05-06 change): NO disarm, NO drain regardless of <paramref name="drainOnLower"/>.
    /// The buffer is now below the new target but audio keeps playing — the drift corrector's
    /// adaptive-gain term (see SessionPlayout) ramps the buffer up to the new target within
    /// seconds without the user ever hearing silence. Previously every raise produced an
    /// audible stop-start because the always-drain path blew the buffer away. Tweaking the
    /// slider in tiny increments is now silent.
    ///
    /// Equal value: no-op.
    /// </summary>
    /// <summary>Legacy single-route setter — operates on the Mixed route. Every classic-mode
    /// call site continues to use this and behaves identically to pre-2026-05-11.</summary>
    public void SetMaxLatencyMs(int value, bool drainOnLower = true) =>
        SetMaxLatencyMs(RenderRoute.Mixed, value, drainOnLower);

    /// <summary>
    /// Per-route setter. Identical algorithm to the legacy one but only drains sessions
    /// tagged with the matching route — so lowering the WASAPI lane's target won't disarm
    /// the ASIO lane's session (and vice versa). In BothIndependent the WASAPI/ASIO routes
    /// have their own slider in the UI driving each call.
    /// </summary>
    public void SetMaxLatencyMs(RenderRoute route, int value, bool drainOnLower = true)
    {
        var clamped = Math.Clamp(value, 1, 500);
        var lane = LatencyFor(route);
        var previousTarget = lane.TargetMs;
        lane.MaxMs = clamped;
        lane.TargetMs = clamped;
        if (clamped < previousTarget && drainOnLower)
        {
            // Only drain sessions on THIS route — leaves other-route sessions playing.
            var snap = sessionsSnapshot;
            foreach (var s in snap)
            {
                if (s.Route != route) continue;
                s.DisarmAndRequestDrain();
            }
        }
    }

    public SessionPlayout GetOrCreateSession(IPEndPoint endpoint, ushort streamId, int capacityBytes)
    {
        var key = (endpoint, streamId);
        lock (sessionsLock)
        {
            if (!sessions.TryGetValue(key, out var sp))
            {
                sp = new SessionPlayout(endpoint, streamId, capacityBytes);
                // Inherit the engine-wide artifact selection so a session created mid-stream
                // gets the right artifact from frame zero (rather than the SessionPlayout
                // default, which would only get overridden on the next SetConcealmentArtifact).
                sp.SetConcealmentArtifact((ConcealmentArtifact)concealmentArtifactRaw);
                sessions[key] = sp;
                sessionsSnapshot = sessions.Values.ToArray();
            }
            return sp;
        }
    }

    public bool RemoveSession(IPEndPoint endpoint, ushort streamId)
    {
        var key = (endpoint, streamId);
        lock (sessionsLock)
        {
            if (sessions.Remove(key, out var sp))
            {
                sp.Dispose();
                sessionsSnapshot = sessions.Values.ToArray();
                return true;
            }
            return false;
        }
    }

    public IReadOnlyList<SessionPlayout> ActiveSessions
    {
        get { lock (sessionsLock) return sessions.Values.ToList(); }
    }

    public void ResetAll()
    {
        lock (sessionsLock)
        {
            foreach (var s in sessions.Values) s.Dispose();
            sessions.Clear();
            sessionsSnapshot = [];
        }
    }

    public long AggregateUnderruns
    {
        get
        {
            long total = 0;
            foreach (var s in sessionsSnapshot) total += s.UnderrunCount;
            return total;
        }
    }

    /// <summary>Per-route underrun aggregator. The continuous auto-tune uses this in
    /// BothIndependent mode so the WASAPI lane's underruns don't make the ASIO auto-tune
    /// skip a tick (and vice versa). In classic modes only the Mixed route has sessions,
    /// so AggregateUnderrunsFor(Mixed) == AggregateUnderruns.</summary>
    public long AggregateUnderrunsFor(RenderRoute route)
    {
        long total = 0;
        foreach (var s in sessionsSnapshot)
        {
            if (s.Route == route) total += s.UnderrunCount;
        }
        return total;
    }

    /// <summary>True if at least one session is currently tagged for this route. Used by
    /// the auto-tune to skip ticking a lane that has nobody to tune — without this gate
    /// the ASIO auto-tune (for example) would react to the shared network-gap signal
    /// populated by WASAPI-lane traffic and silently inflate its own target before the
    /// user has even started an ASIO source, so the next time ASIO actually goes live the
    /// receiver would already be pre-loaded with a high target.</summary>
    public bool HasSessionsForRoute(RenderRoute route)
    {
        foreach (var s in sessionsSnapshot)
        {
            if (s.Route == route) return true;
        }
        return false;
    }

    public long AggregateDrops
    {
        get
        {
            long total = 0;
            foreach (var s in sessionsSnapshot) total += s.DropCount;
            return total;
        }
    }

    /// <summary>Sum of click-trim drop bytes across all active sessions.</summary>
    public long AggregateTrimDropBytes
    {
        get
        {
            long total = 0;
            foreach (var s in sessionsSnapshot) total += s.TrimDropBytes;
            return total;
        }
    }

    /// <summary>Sum of slider-drain drop bytes across all active sessions.</summary>
    public long AggregateDrainDropBytes
    {
        get
        {
            long total = 0;
            foreach (var s in sessionsSnapshot) total += s.DrainDropBytes;
            return total;
        }
    }

    /// <summary>Total click-trim fires (one per trim event, regardless of bytes dropped).</summary>
    public long AggregateTrimFireCount
    {
        get
        {
            long total = 0;
            foreach (var s in sessionsSnapshot) total += s.TrimFireCount;
            return total;
        }
    }

    /// <summary>Cumulative count of single-frame drops the Phase-2 drift corrector has applied.</summary>
    public long AggregateDriftDropFrames
    {
        get
        {
            long total = 0;
            foreach (var s in sessionsSnapshot) total += s.DriftDropFramesTotal;
            return total;
        }
    }

    /// <summary>Cumulative count of single-frame repeats the Phase-2 drift corrector has applied.</summary>
    public long AggregateDriftRepeatFrames
    {
        get
        {
            long total = 0;
            foreach (var s in sessionsSnapshot) total += s.DriftRepeatFramesTotal;
            return total;
        }
    }

    // === WASAPI render thread ===

    /// <summary>
    /// Render-side audio pull. Iterates every session regardless of lane tag and sums them
    /// into one mixed bus. This is what every render backend (WasapiOnly, AsioOnly, classic
    /// Both via the tee, and BothIndependent via the tee) reads from, so a user can pick any
    /// output device for any received audio — independently of which capture technology the
    /// sender used. Per-lane latency targets are still honoured: each session reads its own
    /// route's TargetMs / MaxMs via <see cref="LatencyFor"/>, so the WASAPI-captured stream
    /// can buffer at one latency and the ASIO-captured stream at another within the same
    /// output mix. The lane-specific <see cref="WasapiLaneOutput"/> / <see cref="AsioLaneOutput"/>
    /// surfaces are kept around for future per-route routing options but are not used by the
    /// default render path (see <c>CompositeRenderBackend</c>). 2026-05-11 revision: previous
    /// implementation filtered by route, which made it impossible to route a WASAPI-captured
    /// stream onto an ASIO output (and vice versa) in BothIndependent mode — that broke a
    /// long-standing cross-backend send/receive flow.
    /// </summary>
    public int Read(byte[] buffer, int offset, int count) =>
        ReadAllSessions(buffer, offset, count, mixScratch, sessionScratch, recordDiagnostics: true);

    /// <summary>
    /// Shared per-route render pull. Iterates the session snapshot, summing only those
    /// sessions whose <see cref="SessionPlayout.Route"/> matches the requested filter into
    /// the caller's scratch buffers, applies volume/mute/limiter, and packs to bytes. The
    /// Mixed route additionally feeds <see cref="ReceiverDiagnostics"/> (output-step + buffer
    /// level) — lane routes skip diagnostics to avoid double-counting in BothIndependent mode
    /// where both lanes run their own ReadForRoute concurrently and the legacy single
    /// per-tick stats columns are still the user-visible source of truth.
    /// </summary>
    internal int ReadForRoute(byte[] buffer, int offset, int count, RenderRoute route, float[] mixBuf, float[] sessionBuf, bool recordDiagnostics)
    {
        if (recordDiagnostics) diagnostics.RecordRenderRead(count);

        var outFrames = count / MixBytesPerFrame;
        var outFloats = outFrames * MixChannels;
        // Each route owns its scratch buffers; grow them in place if the consumer is asking
        // for a bigger block than we've ever served before. Per-route ownership means the
        // BothIndependent threads don't fight over one buffer.
        if (mixBuf.Length < outFloats || sessionBuf.Length < outFloats)
        {
            (mixBuf, sessionBuf) = GrowScratch(route, outFloats);
        }
        Array.Clear(mixBuf, 0, outFloats);

        // Snapshot — local copy so the iteration is safe against concurrent dict mutations.
        var snap = sessionsSnapshot;

        // Pull this route's target/max from the per-route state. In Mixed (classic modes)
        // this reads mixedLatency, identical to pre-Stage-4.5 behaviour. In BothIndependent
        // the WASAPI and ASIO route reads pick up their respective LaneLatency entries so
        // each lane's session is paced against its own slider value.
        var routeLatency = LatencyFor(route);
        var routeTargetMs = routeLatency.TargetMs;
        var routeMaxMs = routeLatency.MaxMs;

        var aggregateBufferedBytes = 0;
        var anyContributed = false;
        foreach (var session in snap)
        {
            if (session.Route != route) continue;
            aggregateBufferedBytes += session.BufferedBytes;
            var produced = session.ReadFloats(sessionBuf.AsSpan(0, outFloats), outFrames, routeTargetMs, routeMaxMs, smoothness);
            if (produced <= 0) continue;
            anyContributed = true;
            var summed = produced * MixChannels;
            for (var i = 0; i < summed; i++)
            {
                mixBuf[i] += sessionBuf[i];
            }
        }

        if (recordDiagnostics) diagnostics.RecordBufferLevel(aggregateBufferedBytes);

        if (!anyContributed)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        // Apply volume / mute and the soft-knee tanh limiter before packing. Both the volume
        // knob and the limiter are receiver-engine-wide concerns, so they apply equally to
        // every route (matching the principle that a single user-set volume affects every
        // output device regardless of which lane it belongs to).
        var localVolume = muted ? 0f : volume;
        for (var i = 0; i < outFloats; i++)
        {
            var v = mixBuf[i] * localVolume;
            var sign = v < 0f ? -1f : 1f;
            var abs = v * sign;
            if (abs > LimiterThreshold)
            {
                var excess = abs - LimiterThreshold;
                var compressed = LimiterKnee * MathF.Tanh(excess / LimiterKnee);
                v = sign * (LimiterThreshold + compressed);
            }
            mixBuf[i] = v;
        }

        if (recordDiagnostics) diagnostics.RecordOutputSampleSteps(mixBuf.AsSpan(0, outFloats));

        Buffer.BlockCopy(mixBuf, 0, buffer, offset, outFloats * sizeof(float));
        return count;
    }

    /// <summary>
    /// Read all sessions, regardless of lane tag, into a single mixed bus. Each session is
    /// paced against ITS OWN lane's target/max latency, so a WASAPI-captured session and an
    /// ASIO-captured session in BothIndependent mode each maintain their independent buffer
    /// depths even though they end up in the same output mix. This is the path every render
    /// backend reads from in normal operation — the lane surfaces above are kept for
    /// potential per-output-device routing in a future revision but are not used today.
    /// </summary>
    private int ReadAllSessions(byte[] buffer, int offset, int count, float[] mixBuf, float[] sessionBuf, bool recordDiagnostics)
    {
        if (recordDiagnostics) diagnostics.RecordRenderRead(count);

        var outFrames = count / MixBytesPerFrame;
        var outFloats = outFrames * MixChannels;
        if (mixBuf.Length < outFloats || sessionBuf.Length < outFloats)
        {
            (mixBuf, sessionBuf) = GrowScratch(RenderRoute.Mixed, outFloats);
        }
        Array.Clear(mixBuf, 0, outFloats);

        var snap = sessionsSnapshot;
        var aggregateBufferedBytes = 0;
        var anyContributed = false;
        foreach (var session in snap)
        {
            // Per-session latency: each session's own lane governs its buffer behaviour, so a
            // WASAPI-captured stream can sit at one target depth and an ASIO-captured stream
            // at another. Mixing them at the output level doesn't collapse those targets.
            var laneLatency = LatencyFor(session.Route);
            aggregateBufferedBytes += session.BufferedBytes;
            var produced = session.ReadFloats(sessionBuf.AsSpan(0, outFloats), outFrames, laneLatency.TargetMs, laneLatency.MaxMs, smoothness);
            if (produced <= 0) continue;
            anyContributed = true;
            var summed = produced * MixChannels;
            for (var i = 0; i < summed; i++)
            {
                mixBuf[i] += sessionBuf[i];
            }
        }

        if (recordDiagnostics) diagnostics.RecordBufferLevel(aggregateBufferedBytes);

        if (!anyContributed)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var localVolume = muted ? 0f : volume;
        for (var i = 0; i < outFloats; i++)
        {
            var v = mixBuf[i] * localVolume;
            var sign = v < 0f ? -1f : 1f;
            var abs = v * sign;
            if (abs > LimiterThreshold)
            {
                var excess = abs - LimiterThreshold;
                var compressed = LimiterKnee * MathF.Tanh(excess / LimiterKnee);
                v = sign * (LimiterThreshold + compressed);
            }
            mixBuf[i] = v;
        }

        if (recordDiagnostics) diagnostics.RecordOutputSampleSteps(mixBuf.AsSpan(0, outFloats));
        Buffer.BlockCopy(mixBuf, 0, buffer, offset, outFloats * sizeof(float));
        return count;
    }

    /// <summary>
    /// Grow the per-route scratch buffers in place when a render backend asks for a bigger
    /// block than we've previously served. Writes the new arrays back to the route-owning
    /// fields (so subsequent reads from this route see the larger buffer) and returns them
    /// to the caller for use in the current Read. The Mixed route's buffers live on the
    /// PlayoutEngine itself for legacy reasons; the two lane routes own their buffers on
    /// the corresponding LaneOutput instance. Each route is single-threaded (one consumer
    /// per surface) so we don't need to lock around the realloc.
    /// </summary>
    private (float[] mix, float[] session) GrowScratch(RenderRoute route, int neededFloats)
    {
        switch (route)
        {
            case RenderRoute.Mixed:
                if (mixScratch.Length < neededFloats) mixScratch = new float[neededFloats];
                if (sessionScratch.Length < neededFloats) sessionScratch = new float[neededFloats];
                return (mixScratch, sessionScratch);
            case RenderRoute.WasapiLane:
                if (wasapiLaneOutput.MixScratch.Length < neededFloats) wasapiLaneOutput.MixScratch = new float[neededFloats];
                if (wasapiLaneOutput.SessionScratch.Length < neededFloats) wasapiLaneOutput.SessionScratch = new float[neededFloats];
                return (wasapiLaneOutput.MixScratch, wasapiLaneOutput.SessionScratch);
            case RenderRoute.AsioLane:
                if (asioLaneOutput.MixScratch.Length < neededFloats) asioLaneOutput.MixScratch = new float[neededFloats];
                if (asioLaneOutput.SessionScratch.Length < neededFloats) asioLaneOutput.SessionScratch = new float[neededFloats];
                return (asioLaneOutput.MixScratch, asioLaneOutput.SessionScratch);
            default:
                return (mixScratch, sessionScratch);
        }
    }

    /// <summary>
    /// Per-lane IWaveProvider. Each instance filters PlayoutEngine's session snapshot down
    /// to sessions tagged with a specific <see cref="RenderRoute"/> and runs the standard
    /// volume/mute/limiter pipeline against just that subset. Only meaningful in
    /// BothIndependent mode; in classic modes nothing reads from these surfaces.
    /// </summary>
    private sealed class LaneOutput : IWaveProvider
    {
        private readonly PlayoutEngine owner;
        private readonly RenderRoute route;
        // Each lane owns its own scratch (fields exposed to the owner so ReadForRoute can
        // grow them via the same helper). Public-internal exposure rather than method-call
        // because the grow path needs a ref to the slot, and there's exactly one caller per
        // field — the owner. Keeping these private to the outer class via internal access.
        internal float[] MixScratch = new float[8192];
        internal float[] SessionScratch = new float[8192];

        public WaveFormat WaveFormat => owner.WaveFormat;

        public LaneOutput(PlayoutEngine owner, RenderRoute route)
        {
            this.owner = owner;
            this.route = route;
        }

        public int Read(byte[] buffer, int offset, int count) =>
            owner.ReadForRoute(buffer, offset, count, route, MixScratch, SessionScratch, recordDiagnostics: false);
    }
}
