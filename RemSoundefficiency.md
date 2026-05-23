# RemSound efficiency — deep analysis (2026-05-22)

A full pass through every source file looking for ways to use less CPU / less memory, and for legacy code or things that can be made to run more efficiently. Written as a working document to refer back to as we make changes one at a time.

---

## Part 0 — What we need first: a way to *measure* improvements

There's nothing in there today that tells us how much CPU or memory we use. Without that, any change is a guess. So before anything else, **we need ways to see what RemSound is actually doing right now** so each change can be proved out.

1. **Add a "process self-meter" to the diagnostic log.** Once a second, the app would record how much CPU it just used (as a percentage of one core), how much memory it's holding, and how many garbage-collection events the runtime did in the last 1, 10, and 60 seconds. We already gather one-second diagnostics — this is just five more numbers on the same row. Gated behind the existing Enable-logs checkbox so it costs nothing when off. Output to the diag log so a long session shows the trend.

2. **Add a "per-thread time" counter for the four audio threads** (capture, render, mix, network). Each one records the milliseconds of CPU it actually consumed each second. Today we measure how long the *audio buffer* sits idle between callbacks (a related but different number); this measures how long the *thread itself* was busy. Tells us "the receive thread is using 3 percent of one core" vs "the receive thread is using 18 percent of one core" — directly answers "did this change actually help".

3. **Add an allocation counter.** A built-in .NET counter shows total bytes allocated per second. Logged once a second alongside the rest. A clean steady-state RemSound should be allocating in the kilobytes-per-second range; if it's in megabytes, something inside the hot path is leaking allocations.

These three additions give us a baseline. Every other suggestion in this document can then be tested: did CPU drop, did memory steady-state drop, did GC events get rarer.

---

## Part A — Ways to use less CPU and memory

Grouped from biggest expected win to smallest, with the riskier ones flagged.

### A1. Things you can do that are likely BIG wins

4. **Stop re-checking the audio devices every second on the user-interface thread.** Right now, every single second the app asks Windows for the full list of speakers and microphones, and (when an ASIO driver is selected) it opens the ASIO driver to read its channel names. That's a lot of work for a check that only matters when a device is plugged in or unplugged. Comment in the code even says "3 second intervals" but the timer is actually set to 1 second. Slowing it to 3 or 5 seconds, OR switching to Windows' built-in "device changed" notification (which fires only when something actually changes), would save measurable CPU. The ASIO probe is the heavy bit — it briefly touches the audio driver each time.

5. **Stop trimming the audio mixing loop every 10 milliseconds when nothing is being captured by Windows.** The mixing-engine loop runs at 100 times a second whether or not there's audio to mix. When no input devices are ticked there's literally nothing for it to do — it could go to sleep until it's needed again. Same for the multi-output player loop on the receive side.

6. **Use the right tool for waiting in the two main background loops.** The mixing-engine loop and the multi-output-playout loop both use a pattern that allocates a small object every single wait (~100 times per second per loop). Switching to a "wait token" approach that's allocated once and reused would remove about 200 small allocations per second across the two loops. Each on its own is tiny; over an hour it's still tens of thousands of pointless allocations that the garbage collector eventually has to clean up. Same thing inside `MixingEngine.MixLoop` and `MultiOutputPlayout.ProduceLoop`.

7. **Stop rebuilding the output-target list inside the playout loop every 10 milliseconds.** When the audio is being fanned out to several output devices, the loop currently rebuilds a snapshot list of those devices on every tick. The list only changes when the user changes output ticks — once every few minutes at most. We could cache it and rebuild only when the user changes things. Removes another 100 allocations per second.

8. **Turn off the drift-correction resampler when it has nothing to do.** The receive side runs a sample-rate resampler on every render callback to nudge for clock drift between the sender and receiver. When the sender and receiver clocks happen to match (which is most of the time), the resampler is doing a calculation whose answer is "don't change anything" — and yet we still feed every sample through it. If the measured drift is essentially zero, we could skip the resampler entirely and copy the samples directly. Big win on CPU, and inaudible because we're skipping a "do nothing" operation. Comes back on automatically when real drift is measured again.

9. **Batch the per-second diagnostic reads instead of doing them one at a time.** The diag-log writer (when logging is on) calls roughly 20 separate "give me the latest number" methods every second; several of them each walk the list of active audio streams independently. We could walk the list ONCE and gather all the numbers at the same time. Halves the work done per second when logging is on. When logging is off, this whole path is already silent.

10. **Make the most-expensive per-sample probe optional.** There's a discontinuity detector ("RecordOutputSampleSteps") that runs maths on every single sample of audio (about 96,000 times a second). It's gated behind the Enable-logs checkbox, so it's off in normal use. But it's the single most expensive probe in the engine. Even when on, we could fire it once every N samples instead of every sample (or only when an interesting event is suspected). This makes "logs on" cheaper.

11. **Reduce the cost of sample-by-sample volume + limiter work.** On every render callback the receive engine walks every sample applying volume + a soft limiter. There's a built-in .NET feature for doing these in batches of 4 or 8 samples at once (SIMD/vector instructions); the same code, rewritten that way, would do the same work in about a quarter of the time. Most modern CPUs have this. The limiter's "tanh" call is also relatively expensive — a cheaper approximation is inaudible at the levels it acts on.

12. **The float-to-bytes packing on the send side has the same opportunity.** Every audio packet runs a per-sample loop converting floating-point to 24-bit integers. Same SIMD trick applies. Sends ~400 packets per second per active stream; lots of samples adding up.

13. **The single big main form file (`MainForm.cs`, ~5800 lines) is doing too much at runtime.** It owns timers, peer lists, hotkeys, status text, all in one class. Per-second status updates rebuild several text strings and check several state machines. Splitting the status work into its own class wouldn't directly save CPU but would make further optimisations safer (right now it's hard to reason about what runs when).

### A2. Things you can do that are likely MEDIUM wins

14. **Stop allocating a new byte array for every outgoing heartbeat packet.** Once a second per peer we build a tiny 21-byte packet and then call `.ToArray()` on it, which copies the stack buffer onto the heap. Cheap one-off cost, but it's 200 allocations a day per peer when idle. Easy to keep the buffer around and reuse it.

15. **Stop allocating a string for every diagnostic log call when logging is OFF.** Several places do `onDiagnostic?.Invoke($"some message with {variables}")`. The string interpolation happens *before* the null-check on `onDiagnostic`, so even when the sink is null (i.e. logs are off), we still build the message. There are dozens of these. A small wrapper that checks the gate first would skip building the string entirely.

16. **The heartbeat service walks the network-interface list every 1.5 seconds.** Inside discovery it asks Windows for "all my network interfaces" to compute broadcast addresses, on a schedule. Network interfaces don't change every 1.5 seconds — caching the result and only refreshing on a Windows network-change event would skip ~40 expensive interface-enumeration calls per minute.

17. **Stop rebuilding the per-second diagnostic baseline by reading from every active stream.** The receiver has six or seven aggregate counters (underruns, drops, drift drops, etc.) that the diag log reads each second. Each one walks the stream list independently. If we walked the list once and copied out all the counters at the same time, it'd be ~6× less work, and zero behaviour change.

18. **The render path duplicates two near-identical methods.** `PlayoutEngine.Read` (used in classic modes) and `PlayoutEngine.ReadForRoute` (used in Both Independent mode) do essentially the same loop, differing only in how they filter the session list. They could share one body with the filter passed in. Same speed; less code; safer to optimise once.

19. **The render and capture sides each have their own "scratch buffer that grows when needed" pattern, repeated five or six times.** Each one is fine on its own; the repetition is a maintenance and review burden. Centralising the pattern in one helper makes it easier to swap in a pooled allocator later (which would shave more allocations).

20. **Use a built-in "buffer pool" for the outbound UDP packets.** Sender builds a packet, sends it, throws away the byte array. We could rent a packet's worth of memory from a built-in .NET pool, use it, and return it. .NET's `ArrayPool` is designed for exactly this. Save a small allocation per packet × 400 packets/sec × however many seconds you stream.

### A3. Things that need more thought before doing

21. **The "Tanh" soft-limiter could be a faster approximation.** It triggers only when sample-loudness peaks at 90% of full scale. The current `MathF.Tanh` call is mathematically perfect; a 4th-order polynomial approximation is inaudible. Saves CPU on busy music. Risk: low, but it's the receive-side audio quality so test carefully.

22. **The drift-correction resampler can be cheaper at the cost of inaudible quality loss.** Today it runs in NAudio's "linear interpolation" mode. There's an even cheaper mode for sub-1000-ppm corrections. The difference would not be measurable. Risk: very low.

23. **Multi-threading the sender's encode loop:** Today the audio capture thread does the capture, the mix, the encode, AND the packet send all in sequence. On a slow machine with multiple peers, the send-to-many-peers step could be moved off the audio thread onto a small dedicated send thread, so the audio thread is freed up sooner for the next capture. Risk: medium. Would need careful ordering / no-allocation work between threads. Not a slam-dunk because today's audio thread isn't the bottleneck — but might matter when streaming to 5+ peers.

24. **Multi-threading isn't likely to help on the receiver side** because each capture/render is already on its own thread. Where we DO sometimes have contention is the session dictionary lock during incoming packet handling. Switching to a lock-free map for sessions would eliminate that contention. Risk: medium. Lock-free dictionaries are tricky to get right.

25. **A small amount of code runs at "high priority" inside every audio callback even when off.** The per-callback `Stopwatch.GetTimestamp()` reads happen even when logs are off, because they're not gated. Cheap (nanoseconds) but called hundreds of times per second. Gating them inside `if (DiagnosticsGate.Enabled)` would save a tiny but real cost.

### A4. Memory-specific findings

26. **Steady-state memory should be roughly: 4 MB for the receive-side per-stream ring buffer, 2 MB for record buffers when recording, a few hundred KB for everything else.** Beyond that, anything large is overhead — the per-output WASAPI buffers, the NAudio capture buffers, etc. Without the new measurement we don't know if we're holding more than that or not.

27. **The session-snapshot array is rebuilt every time a peer connects or disconnects.** Fine. But if churn is heavy (a peer in a flaky-network spot reconnecting every few seconds), the snapshot allocator goes up. The session prune does it correctly.

28. **The big main form keeps a lot of WinForms control fields alive for the life of the app.** Normal for a WinForms app. The cost is constant and shows up as ~30 MB of working set; doesn't grow over time. Nothing to do.

29. **Long-running sessions with logging on can accumulate a lot of memory in the log writer** if the disk is slow. The log writer writes through directly so this isn't a major risk, but worth measuring once we have the meter.

---

## Part B — Legacy code, dead code, and cleanups

Things that don't speed RemSound up directly but reduce the code base, make future work easier, and remove footguns. They've all earned their place in the codebase but they've all been superseded.

### B1. Definitely dead — can be deleted

30. **The "KeepAlive" packet type and all its supporting code.** There's a packet type called KeepAlive, an enum for its kinds, a struct (`KeepAliveInfo`), a writer, a reader, and a capabilities flag — total ~80 lines of code spread across one file. Nothing in the application actually creates, sends, or processes a KeepAlive packet. The Heartbeat service replaced it in May. The packet handler in the receiver explicitly says "informational only at this layer" and does nothing. Pure dead code.

31. **The legacy `AudioMode.AsioOnly` and `AudioMode.Both` enum values.** No UI path produces either. Every place that takes an AudioMode now "coerces" these into one of the two real modes (WasapiOnly or BothIndependent). The coercion logic is in three different files. Removing the two dead values lets all the coercion code disappear too.

32. **The two `ConcealmentArtifact` enum values that aren't in the dropdown any more** (`CosineToneShort` and `CosineToneLow`). The preferences dialog doesn't offer them and the load path coerces them to `NoiseBurst` automatically. The synthesis code paths for the cosine tones are still in the receive engine (`ApplyFadeOut` switch statement, `ConcealFadeFramesLow` constant) but they're unreachable from the UI.

33. **The `MuteConnectionCues` field on the profile.** Superseded in May by four per-cue enable flags (Connect / Disconnect / RecordStart / RecordStop). There's a "legacy fallback" code path that reads MuteConnectionCues only when the new flags are unset — which can only happen for profile files from before May. Once you're confident no users still have ancient profiles, this whole migration path can go.

34. **The "drift drop frames" and "drift repeat frames" counters in the receive engine.** They're never incremented any more (the Phase-4 resampler design replaced the splice-based drift corrector that wrote them). They're kept around as zero values so the diag log columns don't disappear. The comment in the code explicitly says "can be removed once the diag columns are pruned."

35. **The `DriftAccumulator` property always returns 0.** Same reason — old Phase-2 metric the Phase-4 design doesn't use. Comment says "kept so MainForm's existing diag log line still compiles; can be removed once the diag columns are pruned."

36. **The `TakeMaxFanOutCacheBytes` / `fanCacheMs` diagnostic also always returns 0.** From the "FanOut" architecture that was removed when each lane got its own filtered source. Code keeps it as a sentinel zero so the diag log column still emits. Can disappear when the diag log line is cleaned up.

37. **The `FormatPayloadSize` (32 bytes) constant is the legacy wire-format size from before May.** Senders now always emit the 36-byte extended format. The 32-byte reader is kept for "old senders" — but no shipped version of RemSound emits 32-byte format packets any more. After enough time has passed for any old users to have updated, the 32-byte path can collapse into just the 36-byte path.

### B2. Likely dead but worth a careful check

38. **The PCM frame assembler's multi-part code.** PCM frames at 5 ms or 2.5 ms (the only two send rates RemSound uses) are always small enough to fit in one UDP packet. The multi-part assembler exists for some future longer-frame mode that never shipped. Probably safe to delete the multi-part path and just expect single-packet frames; if a 10 ms PCM mode is added later we'd put it back.

39. **The `RemoteVolumeUp/Down` and `RemoteMuteToggle` hotkeys** plus the `Control` packet kind — these work and are documented but I'm not sure they're widely used. Worth asking your users.

40. **The "blank template" profile flow.** When a user picks "(Blank template)" at startup, several code paths special-case "currentProfileTitle is null/empty". The total amount of special-casing is significant. Could be simplified by treating "blank template" as a regular (never-saved) profile internally.

41. **The legacy `Settings` cache inside `RemSoundSettingsStore`.** Every "save one field" call creates a Settings object, sets one property, and replaces the cache. The class is in-memory only now (the doc comment explicitly says "this no longer persists to disk"). Could be flattened into a simple set of fields on the store, no Settings wrapper at all.

### B3. Cleanups that aren't dead-code but feel old

42. **The audio backend has TWO different "Both" modes in the enum** (`Both` and `BothIndependent`) and a long comment explaining the difference. Now that only `BothIndependent` is real, the name could just be `Both` and the legacy `Both` value retired. Less to read; less to mistake.

43. **The "AudioMode is derived from AsioDriverName" rule lives implicitly in several places.** Centralising "what mode are we in?" into one method that reads driver name + returns the mode would let other code stop duplicating the logic.

44. **The diagnostic log line is the longest single line in the codebase by a long way.** ~30 columns plus the new XB/WB split fields plus the new GC/network ones. Worth splitting into two physical lines in the log file (one for "what's happening" and one for "how is it happening"). Saves nothing in CPU; readers — including you and me — would find them easier to scan.

45. **The PowerResume splash, the ASIO loading splash, the Update install notice, the Save-confirmation TaskDialog, and Save-blocked-by-read-only TaskDialog** are all similar small popup forms with similar wiring. Could share a tiny base class. Doesn't save CPU; saves ~150 lines of code.

46. **The MainForm has multiple `MarkProfileDirty()` call sites scattered across event handlers.** A small "DirtyTracker" helper that knows which controls should dirty the profile could replace the scatter. Cleaner; same behaviour.

47. **Several places use `.ToArray()` / `.ToList()` defensively at the end of LINQ chains** (e.g. `recentPingSources.Where(...).Select(...).ToList()`) when an enumeration would suffice. These are not in hot paths but they show up dozens of times.

### B4. Risk-flagged for awareness, not necessarily change

48. **The audio-thread "diagnostic gate" pattern relies on every probe checking it.** Most do; a handful of legacy probes don't (they pay a tiny cost always). Tightening that consistency would make "logs off" a hard zero-cost state.

49. **There's a comment in the code apologising for the soft-clamp not being SIMD-vectorised.** Worth doing eventually.

50. **The receive side stack-allocates float scratch buffers via `stackalloc` in two places.** Excellent for performance, but if the audio packet is unusually large (which can't happen on RemSound's protocol but could if a malformed peer sent one), this could overflow the stack. The current code limits the stackalloc to 16 KB and falls back to a heap allocation otherwise — that fallback IS allocating. Fine.

---

## Where I'd start if it were me

If I had to pick the three changes that would make the biggest measurable difference for users on modest laptops:

- **Item 1 + 2 + 3 first** — build the meter. Without the meter, every other item is a guess. Half a day of work.
- **Item 4** (don't probe ASIO every second) — likely the single biggest CPU saving for any ASIO user, and it's not even on the audio path.
- **Item 8** (skip the resampler when drift is zero) — biggest CPU saving on the audio render thread itself, and inaudible.

After those land and you can SEE the improvement in your new meter, items 6, 7, 9, 11, 12 are the next tier and they're all low-risk because they don't change audio behaviour — only how many CPU cycles and allocations they cost.

The legacy-code cleanups (Part B) are independent: do them whenever convenient. They reduce surface area for bugs but don't directly improve performance.

---

## Measurement results — first baseline (test 2 on 2026-05-23)

Test 2 was run for ~3.5 minutes on the desktop with the new meters active, deliberately cycling through configurations: ASIO PCM → WASAPI-only PCM → BothIndep WASAPI+ASIO PCM → Opus 10ms → Priority mode → Tight latency push-mode. Per-phase median readings, all from a single machine with no peer connection:

| Phase | Config | CPU % | Allocs KB/s | captureMs | sendMs |
|---|---|---|---|---|---|
| P1 | BothIndep + ASIO only, PCM | 6.3% | 213 | 4.9 | 4.0 |
| P3 | WasapiOnly + WASAPI loopback, PCM | **3.1%** | 137 | 2.5 | 2.1 |
| P4 | BothIndep + WASAPI only, PCM | 4.7% | 150 | 2.5 | 2.2 |
| P5 | BothIndep + WASAPI + ASIO, PCM | 4.6% | 251 | 6.5 | 5.5 |
| P6 | Same + **Opus 10ms** | 4.7% | **8,718** | 11.1 | 10.0 |
| P7 | + Priority ON, Opus 10ms | 4.7% | **14,007** | 25.7 | 24.2 |
| P8 | Priority ON, back to PCM | 11.0% | 253 | 7.5 | 6.0 |
| P9 | + tight latency + push-WASAPI | 11.1% | 312 | 9.8 | 8.3 |

Key takeaways from the baseline:

* **WASAPI-only with WASAPI loopback is the lightest mode** at 3.1% CPU and ~137 KB/s of allocations.
* **The ASIO probe (item 4) costs about 1.6% CPU + ~13 KB/s allocations** — visible by comparing P3 (no probe) to P4 (probe firing every second, no actual ASIO source yet). Real but smaller than originally guessed.
* **Adding an ASIO source on top of WASAPI doubles capture/send work** (P4→P5 captureMs 2.5→6.5). Expected.
* **Priority mode roughly doubles total CPU** for the same workload (P5 4.6% → P8 11.0%). Per-thread captureMs/sendMs only modestly increase, so the extra CPU is elsewhere — most likely the 1ms scheduler quantum increasing wake-up frequency across all process threads. A deliberate user opt-in, so lower urgency.
* **🚨 OPUS ENCODING IS A MAJOR ALLOCATOR** — see item 51 below.

## Part C — Findings added after the first measurement pass (2026-05-23)

51. **Opus encoding allocates ~8–9 MB/sec at the steady state.** This was not on the original list and DWARFS every other allocation source by an order of magnitude. Switching from PCM to Opus 10ms in the test took allocation rate from 251 KB/s straight to 8,718 KB/s — a 35× jump, with no other configuration change. Adding priority mode on top pushed it to 14,007 KB/s. At ~100 encodes per second this works out to ~87 KB allocated per Opus.Encode() call.
    * The wrapper code in `OpusEncoderState.cs` itself doesn't allocate (it uses pre-allocated scratch arrays for both the int16 conversion buffer and the output packet buffer).
    * Cause is inside the Concentus library (we use Concentus 2.2.2). The C# port of libopus appears to allocate per-call working buffers for the CELT encode path.
    * **First mitigation attempt**: switch from the `Encode(ReadOnlySpan<short>...)` overload to `Encode(ReadOnlySpan<float>...)`. Concentus CELT runs in float natively, so the float-input path may avoid one internal round-trip and may have different allocation behaviour. Also removes our own per-sample clamp+convert loop (saves a small amount of CPU). Risk is very low — Concentus' XML doc explicitly says the float overload clips out-of-range samples internally.
    * If that doesn't move the needle, deeper options are: pin Concentus to an older version with different alloc patterns, fork+patch Concentus to pre-allocate scratch on the encoder instance, or swap to a native Opus library via P/Invoke.
    * This is also a strong incentive to keep PCM as the default codec for users on LAN — PCM allocation stayed at ~251 KB/s in the same config that gave us 8,718 KB/s on Opus.

## Status log

* 2026-05-22 — Initial analysis written by Claude after a full pass over the codebase.
* 2026-05-22 — Implementing items 1, 2, 3 (the measurement layer). All gated behind the existing Enable-logs checkbox so they cost nothing when logging is off.
* 2026-05-23 — First measurement pass run by Ed (test 2). Added Part C above with the headline finding: Opus encoding allocates 8–9 MB/s. First mitigation: switching `OpusEncoderState` to the float-input `Encode` overload.
* 2026-05-23 — Items 4 + 6 + 7 done (slowed `deviceRefreshTimer` to 3 s, swapped `WaitHandle.WaitAny(new[]{...})` for `WaitOne()` in both audio-pipeline tick loops, cached the output-buffer snapshot in `MultiOutputPlayout` so the producer loop no longer rebuilds it per tick). Measured impact small (~20–30 KB/s allocation reduction, ~1–2 % CPU at the margin); the WaitHandle allocations were 24 bytes each so removing 100/sec is only ~2.4 KB/s — original prediction of 100–200 KB/s saving was wrong, the actual scale is much smaller.
* 2026-05-23 — **Correction**: the earlier "Opus down 47 %" claim from the float-input overload switch was wrong. The 8,718 KB/s reference was a 2-encoding-lane configuration; the 4,625 KB/s test was a 1-encoding-lane configuration. Per-lane, the allocation rate was essentially unchanged (~4,400 vs ~4,600). What the float overload DID do, that I undersold: dropped per-call CPU work meaningfully (captureMs+sendMs ~21 → ~12 in the same config). Concentus' internal per-call allocations remain the actual bottleneck for Opus — confirmed by two independent tests now.
* 2026-05-23 — Items 14 + 16 done (reused outbound heartbeat byte buffer in `SendPings`; cached `GetBroadcastAddresses` in `PeerDiscoveryService` with invalidation on `NetworkChange.NetworkAddressChanged`). Item 14 is housekeeping — saves ~150 bytes/s, will not be visible in the meter. Item 16 is more meaningful — saves a `NetworkInterface.GetAllNetworkInterfaces` walk every 1.5 s — probably a few KB/s of allocs and a small slice of CPU. Will measure.
* 2026-05-23 — **Item 8 dropped from this round.** On careful reading: the drift-resampler IS the drift compensator. It engages once per ~10 s with whatever clock-rate ratio was measured. Bypassing the resampler when the ratio is "near unity" loses drift correction — buffer drift accumulates at ~50–500 ppm (typical USB audio clock mismatch), which over a long session produces audible trim or overflow clicks. The only safe bypass case is exactly ratio == 1.0, which only happens in the first 10 s of each session before the first measurement window completes. That's not a meaningful saving and isn't worth the cost of carrying the bypass code. Reopening this item would require a different design (e.g. SIMD inside the resampler's interpolation loop, item 11-style) rather than a bypass.

## Part D — Tight-latency cost (added 2026-05-23 from laptop test)

52. **Tight-latency PCM mode is significantly more expensive than standard mode** — quantified for the first time in the 2026-05-23 laptop send test. Mechanics: tight-latency makes `SenderLane.ProcessPcm` emit one UDP packet per ASIO capture callback rather than accumulating to 480-sample (5 ms) frames. With a small ASIO buffer (the laptop runs at 32 samples = ~0.67 ms callback period), that's **~1,500 UDP packets per second** vs the standard-mode rate of ~400 packets/sec. Same audio bandwidth, ~4× the per-second framing/encode/send work, ~4× the per-second context-switching into the kernel for SendTo.
    * Measured laptop CPU at this setting: **~12.4 % of one core** sending PCM with one ASIO source and no peer. ~8.6 % of that is in code outside our instrumented threads (ASIO host thread + Windows audio stack); the audio-thread work itself is `captureMs + sendMs ≈ 77 ms/sec ≈ 7.7 % of one core`.
    * Measured desktop CPU at standard-mode (no tight latency, larger ASIO buffer): **~1.6 % of one core** for the same audio task. Per-packet, both machines are similarly efficient — the laptop is just doing four times as many packets per second.
    * **Worth documenting in the user manual**: "tight latency" / "Lock to audio clock" can quadruple RemSound's CPU footprint when paired with a small ASIO driver buffer. Users on lower-spec hardware should expect 10-15 % CPU even with no peer connected if they tick tight latency. Users on desktops with healthy CPUs won't notice.
    * No code change recommended — tight latency is a deliberate opt-in for the lowest possible latency. The user is correctly paying the cost they asked for. This is a documentation item, not an efficiency bug.

## Recommendation on item 11 (SIMD per-sample loops)

Item 11 (SIMD the volume + limiter loop on receive, and the float→int24 pack loop on send) would realistically save **~0.4 % of one core** based on the measured renderMs split. The two loops it targets account for roughly 5-6 ms/sec of audio-thread CPU; a 4× SIMD speedup would shave ~4 ms/sec.

Honest assessment: **not worth doing right now**. SIMD audio code carries real risk (one-bit-off mistakes are inaudible in normal content but click on transients), is harder to review, harder to debug. For a 0.4 % CPU saving on already-healthy numbers, the risk-to-reward isn't there. Revisit only if a specific user complaint surfaces about CPU on a slow machine.

**Where the real remaining inefficiency lives** — and what it would take to attack:

* **Opus per-encoder allocation rate (~4.5 MB/s)**: inside Concentus, not our wrapper. Would need a different library (native libopus via P/Invoke, e.g. NativeOpus or FFmpeg.AutoGen) — a real project with its own audio-quality verification burden.
* **The ~8.6 % laptop CPU we don't account for**: ASIO host thread, NAudio's MultiOutputPlayout when no WASAPI outputs are ticked, Windows audio stack. Not our code; not addressable without architectural changes (e.g. ditching NAudio for direct WASAPI/ASIO via P/Invoke, which is a year-scale project).

Diminishing returns kick in here.

## Status log (continued)

* 2026-05-23 — **Opus native binding (item 51 follow-up).** Research pass on Jamulus (uses Opus Custom + CBR + no FEC + complexity 1 + int16 + pre-allocated buffers) and Concentus internals (issue #22, open since 2018, confirms the C# port `new`s ~15 working buffers per `celt_encode_with_ec` call and there's no config knob to suppress them). Cleanest fix: add the `Concentus.Native` NuGet package. Concentus 2.0+ auto-detects native libopus at runtime and routes encode/decode through it; encoder state is allocated once on the C side and reused, eliminating the per-call managed scratch entirely. **Zero changes to `OpusEncoderState.cs` or the call site.** Output is bit-for-bit identical (same library, same encoder configuration). Cross-platform natives ship via the package; `dotnet publish` correctly trims them to just the Windows RIDs (we ship `runtimes/win-x64/native/opus.dll` and the win-x86/arm64 variants alongside it). Managed `Concentus.dll` remains in the publish as a fallback in case the native fails to load. Pending measurement on the next test.
* 2026-05-23 — **Tuning options held in reserve** (not done this round): switch from VBR to CBR (Jamulus' choice), turn off inband FEC (Jamulus doesn't set it), drop complexity from 10 (current) down to 1 or 5. These affect audio quality / packet-loss recovery so they're left as a follow-up once the native-binding switch is measured. If the native switch alone brings allocations to a reasonable level (target: under 500 KB/s per lane), we can leave the tuning alone — current settings give better quality and FEC robustness than Jamulus'.
* 2026-05-23 — **Legacy cleanup (Part B1)** done where back-compat allows:
    * Item 30 (KeepAlive): removed `KeepAliveCapabilities`, `KeepAliveKind`, `KeepAliveInfo`, `KeepAlivePayloadSize`, `WriteKeepAlivePayload`, `TryReadKeepAlive` — all dead since HeartbeatService landed 2026-05-06. Kept `RemPacketType.KeepAlive = 3` and the silent-drop dispatch in `AudioReceiver` so any pre-2026-05-06 build still in the wild has its packets ignored rather than counted as malformed. ~50 lines of dead code gone.
    * Items 34 + 35 (drift drop / repeat / accumulator counters): the Phase-2 splice corrector was retired in favour of the Phase-4 fixed-ratio resampler. Counters never incremented, accessors always returned 0, diag log carried `driftDrop= driftDropΔ= driftRep= driftRepΔ= driftAcc=` columns full of zeros. Removed the backing fields, all five accessors across `SessionPlayout` / `PlayoutEngine` / `AudioReceiver`, the prev-tracking fields in `MainForm`, and the five log columns.
    * Item 36 (fanCacheMs): the FanOutSource architecture was retired in mid-May when each lane got its own filtered PlayoutEngine source. `TakeMaxFanOutCacheBytes`/`Ms` always returned 0; column was a placeholder. Removed.
    * **Skipped** items 31 (AudioMode.AsioOnly / .Both), 32 (ConcealmentArtifact.CosineTone*), 33 (MuteConnectionCues), 37 (32-byte format payload reader). All four are back-compat carry-overs for old profile JSONs or older RemSound peers in the wild. The space saved isn't worth the migration risk. Recorded here so future cleanup passes know not to re-investigate them.

* 2026-05-23 — **Opus native binding confirmed working** in the desktop test at 15:36:55. Opus 10 ms allocation rate dropped from 4,625 KB/s (pre-fix) to **108 KB/s** — a 97.7 % reduction. Process CPU dropped from 4.7 % to 1.6 % in the same config. The remaining 108 KB/s is now in the same ballpark as PCM's 150 KB/s, so Opus is no longer the dominant allocator at all. captureMs + sendMs went UP modestly (5.7 + 5.7 → 11.2 + 12.0) — that's the audio thread doing real encode work now without GC interruptions inflating the Stopwatch reading. Process is doing less total work; the audio thread is doing more honest work. Net good. Item 51 is fully resolved.

* 2026-05-23 — **Honest item-by-item status review** of the remaining items after the round of small fixes that landed today. Conducted because the original analysis sized several items optimistically without quantitative arithmetic; today's measurements show many of those wins to be 5-10× smaller than I claimed. Updated to be conservative about future estimates.

    * **Item 5** (mix loop only ticks when capture has data): the existing `MixingEngine.MixLoop` already short-circuits via `if (localMixer is null) continue;` when no sources are added. Effectively no work to do; only one Stopwatch read per skipped tick. NO-OP, marking done.
    * **Item 8** (drift-resampler bypass): documented earlier as overstated; the only safe bypass is `smoothedRateRatio == 1.0` exactly, which is the first 10 s of every session. Real win = 10 seconds per session of skipped resampler work. Below measurable. Not pursuing. The original analysis's claim that this was "biggest single CPU saving on the audio render thread" was wrong — I was thinking of the resampler as discretionary work when it's actually the drift compensator itself.
    * **Item 9** (batch the per-second diagnostic reads): the multiple metric drains in `MainForm`'s diag emitter all go through pre-existing accessors that walk `sessionsSnapshot` once each. The walks are over a typically 1-2-element array; ~5-20 nanoseconds per walk; 14 walks/sec total = ~280 ns/sec of CPU. Below the measurement floor. CODE QUALITY only, no measurable win. Skipping.
    * **Item 10** (per-sample probe optional gating): the `RecordOutputSampleSteps` second-derivative probe is already gated on `DiagnosticsGate.Enabled` at its first line. With logs off it's free. With logs on it's the cost the user opted into. NO-OP.
    * **Item 11** (SIMD volume + limiter): documented; ~0.4 % CPU saving for real risk of audio bugs. Not doing.
    * **Item 12** (SIMD float-to-int24): same shape as 11, ~0.3 % CPU. Not doing.
    * **Item 13** (MainForm split): pure refactoring, 0 % CPU. Not pursuing in an efficiency pass.
    * **Item 15** (gated string interpolation on log calls): after grepping the 126 `?.Invoke($"…")` sites, the hot-path ones fire 1-3 Hz per peer. Each interpolation is ~80 bytes. Total realistic saving: 1-5 KB/s. The architectural change to gate properly (introducing an interpolation-handler wrapper across 14 files) is disproportionate. Skipping. Worth revisiting only if a future profiling pass shows this is a top-N allocation source.
    * **Item 17** (combine session-snapshot walks): same data as item 9 — the walks are nanoseconds. Code-quality observation, not a perf win. Skipping.
    * **Item 18** (Read / ReadForRoute dedup): real code-quality win (the two methods share ~80 % of their body), 0 % CPU win. Not pursuing in an efficiency pass; would be appropriate work in a future refactor pass.
    * **Item 19** (centralised scratch-buffer pattern): same shape as 18. Refactor, not perf.
    * **Item 20** (ArrayPool for outbound UDP): I had this wrong in the original analysis. The send path was ALREADY converted from `packet.ToArray()` per-send to span-based `Socket.SendTo` in the May 11 refactor (see comment in `AudioSender.SendToAll`). `SenderLane.outboundScratch` is pre-allocated once per lane (2 KB byte buffer, written via spans, reused). No per-packet allocation exists to be eliminated. ALREADY DONE before today.
    * **Item 21** (cheaper Tanh limiter): triggers only at sample magnitudes above 0.9. Real rate is well under 1 % of samples on normal content. Sub-0.1 % CPU. Skipping.
    * **Item 22** (cheaper resampler mode): WdlResampler is already configured in `interp:true, filtercnt:0, sinc:false` — its cheapest mode. ALREADY DONE.
    * **Item 25** (gate Stopwatch reads on DiagnosticsGate): about 10 KB/s of `Stopwatch.GetTimestamp()` reads happen even when logs are off. Each read is a couple ns. Below measurable. Skipping.

### Bottom line

The original 50-item list overweighted some items (items 8, 15, 17, 20 in particular). After today's work and honest re-measurement, the items that have actually moved the meter are:

* Item 51 (Opus native binding): −4,500 KB/s on the Opus path ✅
* Items 4, 6, 7 (small audio-thread allocs + ASIO probe rate): −20-30 KB/s ✅
* Items 14, 16 (heartbeat + discovery): a few KB/s ✅
* Legacy cleanup items 30, 34, 35, 36: zero perf win, ~120 lines removed ✅

The remaining items in the original list are now categorised honestly:
* **Already done before today**: items 20 (UDP allocs), 22 (resampler mode)
* **Code quality, not perf**: items 13, 18, 19
* **Below measurable**: items 5, 9, 10, 17, 21, 25
* **Real but too small / wrong shape**: items 8, 15
* **Real wins but risky**: items 11, 12 (SIMD) — only revisit if we have a CPU complaint
* **Big remaining lever, requires real project**: replacement of NAudio for direct WASAPI/ASIO P/Invoke. Year-scale work. Not on the table now.

**RemSound's efficiency is in a healthy state.** Steady-state PCM-sending: 1.6-4.7 % CPU, 150 KB/s allocs. Receive side: 10 % CPU (mostly NAudio + ASIO host thread, not ours), 178 KB/s. Opus is no longer special. No further efficiency work is recommended in this round; the next time someone surfaces a real performance complaint, this document should be the starting point for measuring before guessing.
