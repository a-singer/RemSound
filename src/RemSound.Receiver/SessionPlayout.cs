using System.Diagnostics;
using System.Net;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// One incoming sender's playout state: its own SPSC ring buffer plus a small set of drift /
/// concealment / smoothness state. Each remote endpoint that's actively sending audio gets
/// exactly one SessionPlayout. <see cref="PlayoutEngine"/> owns the collection and reads from
/// all of them per render callback, summing into the mix bus.
///
/// Drift correction: each sender has its own audio crystal that runs at slightly different
/// rate from the receiver's. This class compensates with a slow integrator that drops or
/// repeats one stereo frame at a time when sustained drift is detected, with a short cosine
/// crossfade across each splice for inaudibility. See <c>DriftGain</c> / <c>DriftCrossfadeFrames</c>.
///
/// Threading: <see cref="Write"/> runs on the network thread (per-sender producer);
/// <see cref="ReadFloats"/> runs on the WASAPI/ASIO render thread (single consumer). The
/// AudioRingBuffer is SPSC-safe; drift / concealment state is only touched from the consumer.
/// </summary>
internal sealed class SessionPlayout : IDisposable
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MixBytesPerFrame = MixChannels * sizeof(float);
    private const int MixBytesPerSecond = MixSampleRate * MixBytesPerFrame;

    private readonly AudioRingBuffer playout;
    // Scratch buffer used by the drift-correction crossfade path. Sized as needed inside
    // ReadFloats; persistent here so we don't reallocate per call.
    private float[] driftScratch = new float[8192];

    private volatile bool playbackArmed;
    private volatile bool drainRequested;

    // Tracks the largest single Write's audio duration in ms — i.e. the active codec's
    // packet-frame size as observed at the buffer level. Used to floor the click-trim
    // margin so we don't false-trim during the natural sawtooth caused by packet
    // arrival (each packet bumps the buffer by frame-ms, then render drains it down).
    // Updated from the network thread; read from the audio thread. Volatile is enough
    // because we only ever monotonically increase it within a session lifetime.
    private volatile int largestWriteMs;

    // === Drop-cause split ===
    // Codex pointed out that the legacy `DropCount` on the ring buffer rolled up every reason
    // we ever dropped audio bytes, making "Drops" in the diag opaque. These per-cause counters
    // let the diag log distinguish:
    //   * trim drops      — smoothness-knob click-trim trimming the buffer toward target
    //   * drain drops     — one-shot drain when the user moves the latency slider
    //   * catastrophic    — TrimFromProducer when the buffer crosses the 1s safety cap
    // (Ring-buffer overflow on Write is still counted in playout.DropCount; we expose that
    // separately.) Each counter is in BYTES so the magnitudes are comparable.
    private long trimDropBytes;
    private long drainDropBytes;
    // Separate count of how many TIMES the click-trim fired (a tiny number tells us frequency,
    // independent of the byte amount).
    private long trimFireCount;

    // === Underrun concealment state ===
    // When the playout ring buffer comes up short on a render-side read, AudioRingBuffer
    // silence-fills the missing portion with hard zero. The transient from the last real
    // sample (amplitude X) to instant zero produces an audible click, especially on PCM
    // (Opus has its own decoder-side PLC for packet loss but doesn't help with audio-thread
    // starvation). We replace that hard zero with a brief envelope from the last real sample
    // down to silence, and a matching envelope back up when audio resumes. The buffer is still
    // silent during a sustained underrun — but the *edges* are smooth, which is where the
    // human ear hears the click. ConcealFadeFramesShort at 32 = ~0.67 ms at 48 kHz.
    //
    // The artifact character is user-pickable (cosine tone short / cosine tone low / noise
    // burst / raw click). Each option uses the same edge-smoothing principle but a different
    // generator for the burst itself; see ApplyFadeOut / ApplyFadeIn.
    private const int ConcealFadeFramesShort = 32;
    private const int ConcealFadeFramesLow = 96;
    // After this many consecutive empty-buffer reads, stop synthesising concealment and just
    // emit silence. Concealment is meant to mask brief transient gaps (a packet late by a few
    // ms); it should NOT fire forever when the sender has actually gone away. Without this
    // guard, killing the sender produced a "shshshsh" tremolo for ~4 s on the receiver — every
    // render callback wrote another noise burst into a buffer that never refilled, until the
    // AudioReceiver's idle-prune (4 s) tore the session down. 8 consecutive empties at typical
    // 5 ms ASIO render = 40 ms of repeated bursts before we give up; covers normal jitter
    // without bleeding into "sender gone" pauses.
    private const int ConcealmentMaxConsecutiveEmpties = 8;
    private bool inUnderrunConcealment;
    private int consecutiveEmptyReads;
    private float lastConcealSampleL;
    private float lastConcealSampleR;
    private volatile int concealmentArtifactRaw = (int)ConcealmentArtifact.NoiseBurst;
    // Per-session RNG for noise concealment. Seeded from process-level Shared so each session
    // gets a different sequence — but we don't care about reproducibility, just character.
    private readonly Random concealRng = new(Random.Shared.Next());

    // === Drift correction (Phase 2, 2026-05-06) ===
    // Continuous low-rate clock-drift correction. The receiver and sender each have their own
    // audio crystal; over time their rates differ by a few-tens-of-ppm (typical for cheap USB
    // audio). Without correction, the playout buffer slowly drifts up (sender faster) or down
    // (sender slower) and eventually clicks via either overflow or underrun.
    //
    // The previous design corrected via a continuously-modulated WdlResampler — which produced
    // sample-level corruption and was the source of all the per-sample artefacts we hunted for
    // weeks (see analysis 2026-05-06). The replacement is the Jamulus / Mumble pattern:
    // **integrate the buffer-level error over time and discretely drop or repeat ONE STEREO
    // FRAME at a time when the integrator signals sustained drift.** A single-frame drop or
    // repeat at 48 kHz is 21 µs of audio — below the threshold of audibility on any normal
    // content, especially when timed by an integrator that fires only on sustained drift, not
    // on packet-arrival jitter.
    //
    // Mechanism per Read:
    //   1. Sample the current buffer level vs target.
    //   2. Integrate (buffer_level_error_frames * dt_sec * DriftGain) into driftAccumulator.
    //   3. If accumulator >= 1, drop one frame from the head of the playout buffer
    //      (sender faster — we've consumed less than it produced; speed up consumption by
    //      one frame). Decrement accumulator.
    //   4. If accumulator <= -1, queue a "repeat one frame" for the next Read (sender slower —
    //      stall consumption by one frame). Increment accumulator.
    //
    // Behaviour by drift rate:
    //   - 0 ppm (perfectly matched clocks): error stays near 0, accumulator stays near 0,
    //     no corrections fire. Silent.
    //   - 50 ppm drift (typical USB crystal mismatch = ~5 frames/sec on 48 kHz): accumulator
    //     grows to ±1 every ~4 seconds; one frame correction every ~4 seconds. 21 µs of audio
    //     dropped or repeated every ~4 seconds. Inaudible.
    //   - Higher transient drift (e.g. system load briefly): integrator catches up within
    //     seconds, brief burst of corrections, then settles. Still inaudible.
    //
    // The existing click-trim block above is kept as a safety net for catastrophic conditions
    // (large step changes that the slow integrator can't keep up with). At normal drift rates
    // the integrator never lets the buffer reach the click-trim threshold, so the trim should
    // effectively never fire in steady-state operation.
    private double driftAccumulatorFrames;
    private long prevDriftSampleTicks;
    private int pendingRepeatFrames;
    private long driftDropFramesTotal;
    private long driftRepeatFramesTotal;
    // Integrator gain. Lowered 2026-05-06 (10×) after an empirical test where the previous
    // gain (0.05) produced ~10 corrections per second on the user's hardware (two free-running
    // USB audio crystals with combined drift around 200 ppm = 10 frames/sec). Even with
    // single-frame corrections, 10 clicks/sec was audible. Lowering the gain alone trades
    // click rate for buffer drift; combined with the crossfade-on-splice change, each
    // correction is also significantly less audible per event.
    //
    // At 0.005, sustained 1-frame error reaches accumulator = 1 in ~200 seconds. For 200 ppm
    // drift (10 frames/sec error growth), the integrator catches up at ~2 corrections/sec
    // steady-state — which combined with crossfaded splices should push perceived click rate
    // toward inaudible.
    //
    // 2026-05-06 (later): added adaptive gain scaling. The base gain above is fine for steady-
    // state clock-drift compensation but pathologically slow when the buffer is far from
    // target — e.g. after a slider raise the buffer sits below target and drift correction
    // takes minutes to fill it. Empirically observed in user testing as "every session sounds
    // different": the buffer wandered for tens of seconds at whatever level the initial
    // arming chaos left it at. Now the effective gain scales linearly with absolute error
    // beyond the small-error band, capped, so:
    //   * |error| <= DriftSmallErrorFrames: gain = DriftGain (today's behaviour, gentle)
    //   * |error|  > DriftSmallErrorFrames: gain = DriftGain × min(|error|/small, maxScale)
    // At 50 frames (~1 ms) the gain is 1×; at 1000 frames (~21 ms) it's 20× capped, giving
    // a fill rate of ~100 frames/sec — a 20 ms slider raise converges in ~10 seconds with
    // a barely-audible 0.2% rate offset during the fill.
    private const double DriftGain = 0.005;
    // Below this absolute error, gain stays at the steady-state baseline. ~1 ms at 48 kHz.
    private const double DriftSmallErrorFrames = 50;
    // Cap on adaptive-gain scale, so even huge errors don't produce an audible time-stretch
    // (200/sec frame edits = 0.42% rate change, edge of noticeable on tonal content).
    private const double DriftMaxGainScale = 20.0;
    // Number of stereo frames each side of a splice point that get blended when a drop or
    // repeat fires. Cosine crossfade over this window smooths the discontinuity into an audio
    // characteristic that's much harder to perceive as a click. 8 frames = 167 µs at 48 kHz —
    // shorter than a typical impulse response, so the smear doesn't blur transients audibly.
    private const int DriftCrossfadeFrames = 8;
    // Pending corrections (sample-aligned single-frame edits at the next Read).
    private int pendingDropFrames;
    // Public accessors for the diag log.
    public long DriftDropFramesTotal => Interlocked.Read(ref driftDropFramesTotal);
    public long DriftRepeatFramesTotal => Interlocked.Read(ref driftRepeatFramesTotal);

    public IPEndPoint Endpoint { get; }
    /// <summary>The stream ID this session was opened for. Sessions are keyed by
    /// (Endpoint, StreamId) so a single peer can produce multiple simultaneous streams
    /// (e.g. WASAPI lane + ASIO lane in the native-independent mode). For single-lane
    /// modes there's still one session per peer with whatever streamId the sender chose
    /// (currently 1).</summary>
    public ushort StreamId { get; }
    /// <summary>Which render route this session's audio belongs to. Set by AudioReceiver
    /// from the format packet's Lane byte at session-creation (and updated on the rare
    /// in-place format change that keeps the same SessionPlayout alive). PlayoutEngine
    /// uses this to decide which of its per-route IWaveProvider surfaces this session
    /// contributes to. Defaults to <see cref="RenderRoute.Mixed"/> — the value an old
    /// sender or a classic-mode (WasapiOnly / AsioOnly / Both) sender writes.</summary>
    public RenderRoute Route { get; set; } = RenderRoute.Mixed;
    public int BufferedBytes => playout.BufferedBytes;
    public int BufferedMs => playout.BufferedBytes / MixBytesPerFrame * 1000 / MixSampleRate;
    public long UnderrunCount => playout.UnderrunCount;
    public long DropCount => playout.DropCount;
    public bool IsArmed => playbackArmed;

    /// <summary>Per-cause drop accessors (cumulative bytes / counts since session start).
    /// Splits the previously-opaque DropCount so the diag log can distinguish click-trim
    /// from drain-on-knob-change from ringbuffer overflow. AggregateDrops on the engine
    /// continues to expose the rolled-up total for back-compat.</summary>
    public long TrimDropBytes => Interlocked.Read(ref trimDropBytes);
    public long DrainDropBytes => Interlocked.Read(ref drainDropBytes);
    public long TrimFireCount => Interlocked.Read(ref trimFireCount);

    /// <summary>Sets the concealment artifact this session's playout uses on underrun gaps.
    /// Takes effect on the very next gap; no need to restart playback. Receiver-side only —
    /// the sender doesn't see this and wouldn't behave differently if it did.</summary>
    public void SetConcealmentArtifact(ConcealmentArtifact value) =>
        concealmentArtifactRaw = (int)value;

    /// <summary>UTC time of the most recent successful audio write. Used by <see cref="AudioReceiver"/>
    /// to prune long-idle sessions so the dictionary doesn't grow unboundedly.</summary>
    public DateTime LastWriteUtc { get; private set; } = DateTime.UtcNow;

    public SessionPlayout(IPEndPoint endpoint, ushort streamId, int capacityBytes)
    {
        Endpoint = endpoint;
        StreamId = streamId;
        playout = new AudioRingBuffer(capacityBytes);
    }

    public void Write(ReadOnlySpan<byte> source)
    {
        var ms = source.Length * 1000 / MixBytesPerSecond;
        if (ms > largestWriteMs) largestWriteMs = ms;
        playout.Write(source);
        LastWriteUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Network-thread callback after a frame has been queued. Arms playback the moment this
    /// session's buffer first reaches the user's target; subsequent reads then engage the
    /// drift corrector. Each session arms independently, so a newly-arrived sender can start
    /// playing without waiting for already-armed sessions.
    ///
    /// Also enforces a CATASTROPHIC-only cap on buffer level: if audio piles up beyond 1 second
    /// (because the render thread hasn't started yet, or got stuck), we trim down to 250 ms.
    /// The threshold is intentionally far above any reasonable jitter cushion — earlier we used
    /// 3× target which fought with the drift corrector on a noisy WAN (target 10 ms, real
    /// jitter up to 76 ms ⇒ buffer was being trimmed every second, causing the very clicking
    /// it was supposed to avoid). Now the cap is purely a safety net against catastrophic
    /// backlogs (multi-second pile-ups while no consumer exists); ordinary jitter is absorbed
    /// by the buffer + drift corrector + click-trim combo.
    /// </summary>
    public void NoteFramesQueued(int targetLatencyMs)
    {
        const int CatastrophicCapMs = 1000;
        const int CatastrophicTrimToMs = 250;
        if (playout.BufferedBytes > MillisecondsToBytes(CatastrophicCapMs))
        {
            playout.TrimFromProducer(MillisecondsToBytes(CatastrophicTrimToMs));
        }

        if (playbackArmed) return;
        if (playout.BufferedBytes >= MillisecondsToBytes(Math.Max(targetLatencyMs, 1)))
        {
            playbackArmed = true;
        }
    }

    /// <summary>Disarm and request a drain on the next read — used when the user raises or
    /// lowers the latency knob. The mix bus continues with whatever's already armed.</summary>
    public void DisarmAndRequestDrain()
    {
        playbackArmed = false;
        drainRequested = true;
    }

    /// <summary>Reset the buffer and per-session state. Used at start/stop. Arming will rebuild
    /// from the next packets that arrive.</summary>
    public void Reset()
    {
        playout.Reset();
        playbackArmed = false;
        largestWriteMs = 0;
        inUnderrunConcealment = false;
        consecutiveEmptyReads = 0;
        lastConcealSampleL = 0f;
        lastConcealSampleR = 0f;
        driftAccumulatorFrames = 0;
        prevDriftSampleTicks = 0;
        pendingDropFrames = 0;
        pendingRepeatFrames = 0;
    }

    public void Dispose()
    {
        // AudioRingBuffer is managed; nothing to free explicitly. Method present for symmetry
        // with Stream/Capture sessions and to allow future per-session unmanaged state.
    }

    /// <summary>
    /// WASAPI/ASIO render thread. Pulls <paramref name="outFrames"/> stereo frames from this
    /// session's playout ring into <paramref name="output"/>, applying drift correction and
    /// underrun concealment along the way. Returns the count of frames actually produced; if
    /// the session is disarmed (or drained completely) the return is 0 and
    /// <paramref name="output"/> is untouched (caller is responsible for zero-fill).
    /// </summary>
    public int ReadFloats(Span<float> output, int outFrames, int targetLatencyMs, int currentMaxLatencyMs, int smoothness = 3)
    {
        // Drain on user knob change.
        if (drainRequested)
        {
            drainRequested = false;
            var targetBytes = MillisecondsToBytes(targetLatencyMs);
            var buffered = playout.BufferedBytes;
            if (buffered > targetBytes)
            {
                var bytes = buffered - targetBytes;
                playout.DropOldest(bytes);
                Interlocked.Add(ref drainDropBytes, bytes);
            }
        }

        if (!playbackArmed)
        {
            return 0;
        }

        // NOTE: there used to be an "auto-disarm if buffer empty" block here. It was added
        // 2026-04-30 to clean up phantom underrun counts after a peer disconnect (the
        // 4-second idle-prune fires later, so without auto-disarm the underrun counter would
        // climb at ~100/sec while we waited). The comment claimed "mix output unchanged
        // either way" — but that was wrong at tight target latency.
        //
        // What auto-disarm did wrong: any time the buffer dipped to zero even momentarily
        // (ordinary sender-side mix-tick jitter — a 16ms gap between packets is normal on
        // Windows), it would disarm the session and return 0. ReadFloats then output silence
        // until packets refilled the buffer ALL THE WAY BACK to the user's target latency
        // and NoteFramesQueued re-armed. Each transient underrun became a 10-15ms silence
        // gap instead of a few-ms pad. That's the audible click that Ed kept hearing on
        // localhost at target=10 vs the older build that ran clean.
        //
        // Now: a momentary empty buffer just produces a small silence-pad on this Read (the
        // underrun counter still increments via playout.ReadFloats's own silence-fill — fine,
        // that's diagnostic noise not audio noise). The 4-second idle-prune in AudioReceiver
        // still handles long-term disconnect by tearing the session down entirely. No auto-
        // disarm needed.

        // === Click-based buffer-smoothness trim ===
        //
        // The Buffer-smoothness knob (1 = aggressive, 10 = smooth) controls how aggressively
        // we DROP oldest samples when the buffer drifts above target. When (bufferedMs >
        // target + trimMargin) we drop the excess down to target. Causes a brief click at the
        // drop point but holds the queue right at the user's chosen latency.
        //
        // Largely a safety net post-Phase 2: the drift corrector below keeps the buffer near
        // target in normal operation, so the trim only fires under catastrophic conditions
        // (large step changes the slow integrator can't keep up with). Replaced an earlier
        // resampler-rate controller that pitch-shifted music while correcting drift — clicks
        // turned out to be the lesser evil, and the trim itself is a direct DropOldest on the
        // ring buffer (no resampler involved), so it's guaranteed to fire when needed.
        //
        // Margin and drop-destination computation. SPLIT BY KNOB:
        //
        // smoothness == 1 ("stupid aggressive" — used in ASIO Tight Latency mode for
        // sub-10 ms target):
        //   floor       = largestWriteMs * 2 + 4 (min 4)
        //   drop-to     = target + largestWriteMs (one packet's cushion only)
        //   This is the original "reconnect-feel" tightness — tight threshold,
        //   tight drop, frequent clicks but glued to target. Don't touch this. It's
        //   what makes ASIO at target=1 ms snap back to target after every burst.
        //
        // smoothness >= 2:
        //   floor       = largestWriteMs * 4 + 4 (min 15)
        //   drop-to     = target + largestWriteMs * 2 + 5 (covers render-period +
        //                 frame + jitter pad)
        //   The looser values prevent default-smoothness-3 from tripping on routine
        //   startup bursts (96 kHz device + Opus 5 ms saw a 40 ms initial buffer that
        //   exceeded the old default threshold of 32 ms — the rate controller would
        //   have handled it in a few seconds, but trim fired and dropped the buffer
        //   too low to absorb normal sender jitter, causing 20 underruns/sec for the
        //   rest of the session).
        //
        // Direct evidence: localhost 96 kHz Opus test 2026-05-02 06:37 with
        // smoothness=3 — drops=9600 from one startup trim, then 20 underruns/sec.
        // Subsequent 48 kHz test with same smoothness ran clean because trim never
        // fired (buffer never quite reached the 32 ms threshold). The looser default
        // floor lets the rate controller handle initial transients on either device
        // rate.
        //
        // Examples at target=10 ms:
        //   smoothness=1:
        //     PCM Tight 2.5: floor=8.   drop to 12.5. trims at >18.
        //     Opus 10:       floor=24.  drop to 20.   trims at >34.
        //   smoothness=3 (default):
        //     PCM Tight 2.5: floor=15.  drop to 19.   trims at >33.
        //     PCM 5:         floor=24.  drop to 25.   trims at >42.
        //     Opus 10:       floor=44.  drop to 35.   trims at >62.
        var clampedKnob = Math.Clamp(smoothness, 1, 10);
        var aggressive = clampedKnob == 1;
        var floorMarginMs = aggressive
            ? Math.Max(largestWriteMs * 2 + 4, 4)
            : Math.Max(largestWriteMs * 4 + 4, 15);
        var dropToCushionMs = aggressive
            ? largestWriteMs
            : largestWriteMs * 2 + 5;
        var knobExtraMs = clampedKnob switch
        {
            1 => 0,
            2 => 3,
            3 => 8,
            4 => 16,
            5 => 28,
            6 => 45,
            7 => 70,
            8 => 110,
            9 => 200,
            _ => -1, // 10 = no trim
        };
        if (knobExtraMs >= 0)
        {
            var trimMarginMs = floorMarginMs + knobExtraMs;
            var trimThresholdBytes = MillisecondsToBytes(targetLatencyMs + trimMarginMs);
            if (playout.BufferedBytes > trimThresholdBytes)
            {
                var keepBytes = MillisecondsToBytes(Math.Max(targetLatencyMs + dropToCushionMs, 1));
                var dropBytes = playout.BufferedBytes - keepBytes;
                if (dropBytes > 0)
                {
                    playout.DropOldest(dropBytes);
                    Interlocked.Add(ref trimDropBytes, dropBytes);
                    Interlocked.Increment(ref trimFireCount);
                }
            }
        }

        // === Drift correction (Phase 2) ===
        //
        // Continuously integrate buffer-level error and drop / repeat single frames at low
        // rate to keep buffer aligned with target despite clock-drift between sender and
        // receiver crystals. Replaces the continuous adaptive resampling that produced
        // sample-level artefacts (analysed 2026-05-06). See the field-block comment above
        // for the design rationale.
        //
        // SAMPLE-RATE MISMATCH (future): the direct read below requires input PCM to already
        // be at MixSampleRate (48 kHz). When endpoints have mismatched device rates (e.g.
        // one machine at 44.1 kHz), the sender's MixingEngine still resamples to 48 kHz on
        // the capture side so the wire format is consistent — but if a future change emits
        // at the source's native rate, we'd need a FIXED-ratio resampler here (input_rate /
        // 48000, computed once, never modulated). The continuous-modulation pattern was the
        // bug; a fixed ratio is fine.
        var driftTicks = Stopwatch.GetTimestamp();
        var driftTargetBytes = MillisecondsToBytes(targetLatencyMs);
        if (prevDriftSampleTicks != 0)
        {
            var dtSec = (driftTicks - prevDriftSampleTicks) / (double)Stopwatch.Frequency;
            var errorFrames = ((double)playout.BufferedBytes - driftTargetBytes) / MixBytesPerFrame;
            // Adaptive gain: baseline at small errors (gentle steady-state compensation for
            // clock drift) but accelerated at large errors (fast convergence after a slider
            // raise or initial arming overshoot). Without this, the buffer can sit at any
            // level between 0 and target+jitter for tens of seconds — making sessions feel
            // randomly different. With this, the buffer reliably reaches target within a few
            // seconds of any disturbance.
            var absErrorFrames = errorFrames < 0 ? -errorFrames : errorFrames;
            var gainScale = absErrorFrames <= DriftSmallErrorFrames
                ? 1.0
                : Math.Min(absErrorFrames / DriftSmallErrorFrames, DriftMaxGainScale);
            driftAccumulatorFrames += errorFrames * dtSec * DriftGain * gainScale;
            // Clamp to prevent runaway in pathological conditions (e.g. session pause).
            if (driftAccumulatorFrames > 100.0) driftAccumulatorFrames = 100.0;
            else if (driftAccumulatorFrames < -100.0) driftAccumulatorFrames = -100.0;
        }
        prevDriftSampleTicks = driftTicks;

        // Queue at most one correction per Read so corrections spread evenly rather than burst.
        if (driftAccumulatorFrames >= 1.0)
        {
            pendingDropFrames++;
            driftAccumulatorFrames -= 1.0;
        }
        else if (driftAccumulatorFrames <= -1.0)
        {
            pendingRepeatFrames++;
            driftAccumulatorFrames += 1.0;
        }

        // === Read with optional crossfaded drop / repeat ===
        //
        // The trick to audibly-clean drift correction: don't perform the splice as a hard
        // cut. Read one extra frame (drop) or one fewer frame (repeat) from the buffer, then
        // CROSSFADE around the splice point over DriftCrossfadeFrames samples. The cosine
        // window blends the audio either side of the splice into a smooth smear instead of
        // a discontinuity. At 8 frames (~167 µs at 48 kHz) the smear is much shorter than
        // any audible transient and far less perceptible than the original sample-level
        // discontinuity.
        //
        // Splice position: middle of the output buffer. Could choose a low-amplitude moment
        // for further inaudibility (PSOLA-style) but middle-of-buffer is good enough on
        // typical content and keeps the code simple.
        var dropThisCall = pendingDropFrames > 0 && outFrames > DriftCrossfadeFrames * 2 ? 1 : 0;
        var repeatThisCall = pendingRepeatFrames > 0 && outFrames > DriftCrossfadeFrames * 2 ? 1 : 0;
        // Don't try to do both in the same Read; they'd cancel anyway.
        if (dropThisCall > 0 && repeatThisCall > 0) { dropThisCall = 0; repeatThisCall = 0; }

        if (dropThisCall > 0)
        {
            // Read outFrames + 1 frames into the output span by reading the first half,
            // skipping the splice with crossfade, then reading the second half. We need
            // a small extra-sample scratch for the splice. Reuse driftScratch as
            // temp storage (it's already managed and grows with outFrames).
            var extraFloats = (outFrames + 1) * MixChannels;
            if (driftScratch.Length < extraFloats)
            {
                driftScratch = new float[extraFloats];
            }
            var temp = driftScratch.AsSpan(0, extraFloats);
            ReadInputWithConcealment(temp);
            // Crossfade the splice. Splice position = midpoint of the output frame.
            // Result: outFrames samples where one is "elided" via a cosine cross-blend.
            ApplyDropCrossfade(temp, output, outFrames);
            pendingDropFrames--;
            Interlocked.Increment(ref driftDropFramesTotal);
        }
        else if (repeatThisCall > 0)
        {
            // Read outFrames - 1 frames into temp, then expand to outFrames via a crossfaded
            // insertion at the splice point.
            var shortFloats = (outFrames - 1) * MixChannels;
            if (driftScratch.Length < shortFloats)
            {
                driftScratch = new float[shortFloats];
            }
            var temp = driftScratch.AsSpan(0, shortFloats);
            ReadInputWithConcealment(temp);
            ApplyRepeatCrossfade(temp, output, outFrames);
            pendingRepeatFrames--;
            Interlocked.Increment(ref driftRepeatFramesTotal);
        }
        else
        {
            ReadInputWithConcealment(output);
        }
        return outFrames;
    }

    /// <summary>Drop-mode crossfade: temp has (outFrames + 1) frames, output gets outFrames
    /// frames with one elided at the splice via a cosine blend across DriftCrossfadeFrames
    /// samples on each side.</summary>
    private static void ApplyDropCrossfade(ReadOnlySpan<float> temp, Span<float> output, int outFrames)
    {
        // Splice at midpoint of output frames. The "skipped" sample in temp lives at index
        // spliceIdx; either side of it gets cross-blended.
        var spliceIdx = outFrames / 2;
        var window = DriftCrossfadeFrames;
        var halfWindow = window / 2;

        // Pre-window: copy temp[0..spliceIdx-halfWindow] verbatim.
        var preEnd = spliceIdx - halfWindow;
        if (preEnd > 0)
        {
            temp.Slice(0, preEnd * MixChannels).CopyTo(output);
        }

        // Window: cosine crossfade. As we walk through `window` output frames, blend from
        // temp[preEnd + k] (the "before-skip" sample) toward temp[preEnd + 1 + k] (the
        // "after-skip" sample). The blend mixes consecutive temp positions so the splice
        // is spread out smoothly.
        for (var k = 0; k < window; k++)
        {
            var t = (k + 1) / (double)(window + 1);
            // Cosine-shaped smooth fade from 0 to 1 across the window.
            var fadeIn = (float)((1.0 - Math.Cos(Math.PI * t)) * 0.5);
            var fadeOut = 1f - fadeIn;
            var beforeIdx = (preEnd + k) * MixChannels;
            var afterIdx = (preEnd + 1 + k) * MixChannels;
            var dstIdx = (preEnd + k) * MixChannels;
            output[dstIdx] = temp[beforeIdx] * fadeOut + temp[afterIdx] * fadeIn;
            output[dstIdx + 1] = temp[beforeIdx + 1] * fadeOut + temp[afterIdx + 1] * fadeIn;
        }

        // Post-window: copy temp[spliceIdx+halfWindow+1..outFrames+1] to output[spliceIdx+halfWindow..outFrames].
        // The "+1" on the source side is the elision: we skip one frame from temp.
        var postStartTemp = spliceIdx + halfWindow + 1;
        var postStartOut = spliceIdx + halfWindow;
        var postLen = outFrames - postStartOut;
        if (postLen > 0)
        {
            temp.Slice(postStartTemp * MixChannels, postLen * MixChannels)
                .CopyTo(output.Slice(postStartOut * MixChannels));
        }
    }

    /// <summary>Repeat-mode crossfade: temp has (outFrames - 1) frames, output gets outFrames
    /// with one synthesised at the splice via a cosine blend that "stretches" temp by one
    /// frame.</summary>
    private static void ApplyRepeatCrossfade(ReadOnlySpan<float> temp, Span<float> output, int outFrames)
    {
        var spliceIdx = outFrames / 2;
        var window = DriftCrossfadeFrames;
        var halfWindow = window / 2;

        // Pre-window: copy temp[0..spliceIdx-halfWindow] verbatim.
        var preEnd = spliceIdx - halfWindow;
        if (preEnd > 0)
        {
            temp.Slice(0, preEnd * MixChannels).CopyTo(output);
        }

        // Window of (window + 1) output frames mapped to (window) temp frames. Cosine
        // crossfade synthesizes the extra frame: each output sample in the window is a
        // blend of two adjacent temp samples, with the blend weight progressing slower than
        // the index, effectively inserting a "smoothed" extra sample.
        for (var k = 0; k <= window; k++)
        {
            var t = k / (double)(window + 1);
            var fadeIn = (float)((1.0 - Math.Cos(Math.PI * t)) * 0.5);
            var fadeOut = 1f - fadeIn;
            // Map output index -> temp position: output[preEnd+k] takes from temp[preEnd+k-1] and temp[preEnd+k].
            // For k=0 we use temp[preEnd] alone; for k=window we use temp[preEnd+window-1] alone.
            var leftTempIdx = Math.Max(0, preEnd + k - 1) * MixChannels;
            var rightTempIdx = Math.Min(temp.Length / MixChannels - 1, preEnd + k) * MixChannels;
            var dstIdx = (preEnd + k) * MixChannels;
            output[dstIdx] = temp[leftTempIdx] * fadeOut + temp[rightTempIdx] * fadeIn;
            output[dstIdx + 1] = temp[leftTempIdx + 1] * fadeOut + temp[rightTempIdx + 1] * fadeIn;
        }

        // Post-window: copy temp[spliceIdx+halfWindow..outFrames-1] to output[spliceIdx+halfWindow+1..outFrames].
        var postStartTemp = spliceIdx + halfWindow;
        var postStartOut = spliceIdx + halfWindow + 1;
        var postLen = outFrames - postStartOut;
        if (postLen > 0)
        {
            temp.Slice(postStartTemp * MixChannels, postLen * MixChannels)
                .CopyTo(output.Slice(postStartOut * MixChannels));
        }
    }

    /// <summary>
    /// Wraps <see cref="AudioRingBuffer.ReadFloats"/> with packet-loss-style concealment.
    /// On a short read, replaces the silence-filled tail with a brief synthesised burst
    /// (character chosen by <see cref="SetConcealmentArtifact"/>) decaying to zero. On the
    /// next full read after a gap, applies a matching fade-in so the resumed audio doesn't
    /// start with a hard discontinuity. The result is a smooth attack-and-release at the
    /// edges of any gap — the human ear is much more forgiving of "dipped briefly then came
    /// back" than of "instant click into silence and instant click back".
    ///
    /// Stereo-only (matches the rest of the audio path). Output flows through the mix bus
    /// and limiter as usual.
    /// </summary>
    private void ReadInputWithConcealment(Span<float> inSpan)
    {
        var requestedFloats = inSpan.Length;
        var floatsRead = playout.ReadFloats(inSpan);
        var requestedFrames = requestedFloats / MixChannels;
        var framesRead = floatsRead / MixChannels;

        var artifact = (ConcealmentArtifact)concealmentArtifactRaw;

        if (framesRead < requestedFrames)
        {
            // Don't synthesise concealment forever during a sustained empty-buffer state — the
            // sender has probably gone away. After N consecutive empty reads we just leave the
            // buffer's hard-zero in place; result is true silence rather than a "shshshsh"
            // tremolo as the noise/cosine artifact retriggers each render callback.
            consecutiveEmptyReads = framesRead == 0 ? consecutiveEmptyReads + 1 : 0;
            if (consecutiveEmptyReads <= ConcealmentMaxConsecutiveEmpties)
            {
                // AudioRingBuffer silence-filled inSpan[floatsRead..] with zero. Replace the
                // head of that silence with the chosen artifact, then leave the rest at zero.
                var silenceFrameStart = framesRead;
                var silenceFrameCount = requestedFrames - framesRead;
                ApplyFadeOut(inSpan, silenceFrameStart, silenceFrameCount, artifact);
            }
            inUnderrunConcealment = true;
        }
        else if (inUnderrunConcealment)
        {
            // First full read after a gap. Fade the new audio in from zero so we don't
            // instantly jump back to whatever the new audio's amplitude is.
            ApplyFadeIn(inSpan, requestedFrames, artifact);
            inUnderrunConcealment = false;
            consecutiveEmptyReads = 0;
        }
        else
        {
            consecutiveEmptyReads = 0;
        }

        // Remember the last real sample for the next fade-out. Use the last frame of actual
        // ring data, not anything we just synthesised. (Only meaningful if we read at least
        // one real frame this call — i.e. framesRead > 0.)
        if (framesRead > 0)
        {
            var lastIdx = (framesRead - 1) * MixChannels;
            lastConcealSampleL = inSpan[lastIdx];
            lastConcealSampleR = inSpan[lastIdx + 1];
        }
    }

    /// <summary>Synthesises the fade-out burst for the chosen artifact into the silence
    /// region starting at <paramref name="startFrame"/>. Click variant leaves the buffer's
    /// hard-zero in place.</summary>
    private void ApplyFadeOut(Span<float> inSpan, int startFrame, int silenceFrameCount, ConcealmentArtifact artifact)
    {
        if (artifact == ConcealmentArtifact.Click) return; // Hard zero; produces the original click.

        var fadeLen = artifact == ConcealmentArtifact.CosineToneLow
            ? ConcealFadeFramesLow
            : ConcealFadeFramesShort;
        var fadeFrames = Math.Min(fadeLen, silenceFrameCount);
        for (var f = 0; f < fadeFrames; f++)
        {
            // Common envelope: cosine ramp from 1.0 → 0.0 across the fade region.
            var t = (f + 1) / (double)fadeFrames;
            var g = (float)((Math.Cos(Math.PI * t) + 1.0) * 0.5);
            var idx = (startFrame + f) * MixChannels;
            switch (artifact)
            {
                case ConcealmentArtifact.NoiseBurst:
                    // White noise at last-sample peak amplitude. Random per channel — broader
                    // stereo image than mono noise, and avoids correlated content the brain
                    // can latch onto as a tone.
                    var peak = Math.Max(Math.Abs(lastConcealSampleL), Math.Abs(lastConcealSampleR));
                    inSpan[idx] = ((float)concealRng.NextDouble() * 2f - 1f) * peak * g;
                    inSpan[idx + 1] = ((float)concealRng.NextDouble() * 2f - 1f) * peak * g;
                    break;
                default:
                    // Cosine-tone variants (short/low). Hold last sample, scaled by envelope.
                    inSpan[idx] = lastConcealSampleL * g;
                    inSpan[idx + 1] = lastConcealSampleR * g;
                    break;
            }
        }
    }

    /// <summary>Fades the resumed audio in from zero with the same cosine envelope used on
    /// the way out. Click variant skips the fade — the goal of "Click" is to expose the
    /// original raw zero-fill behaviour, including its discontinuity at audio resumption.</summary>
    private static void ApplyFadeIn(Span<float> inSpan, int requestedFrames, ConcealmentArtifact artifact)
    {
        if (artifact == ConcealmentArtifact.Click) return;

        var fadeLen = artifact == ConcealmentArtifact.CosineToneLow
            ? ConcealFadeFramesLow
            : ConcealFadeFramesShort;
        var fadeFrames = Math.Min(fadeLen, requestedFrames);
        for (var f = 0; f < fadeFrames; f++)
        {
            var t = f / (double)fadeFrames;
            var g = (float)((1.0 - Math.Cos(Math.PI * t)) * 0.5);
            var idx = f * MixChannels;
            inSpan[idx] *= g;
            inSpan[idx + 1] *= g;
        }
    }

    private static int MillisecondsToBytes(int milliseconds) =>
        Math.Max(MixBytesPerFrame, milliseconds * MixBytesPerSecond / 1000);
}
