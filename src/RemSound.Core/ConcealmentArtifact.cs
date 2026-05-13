namespace RemSound.Core;

/// <summary>
/// What kind of audio the receiver synthesises across an underrun gap. The receiver-side
/// playout buffer can come up empty for a few frames if the network is late or if the local
/// audio thread woke up faster than packets arrived; the original behaviour was a hard zero
/// (audible click). 2026-05-04 introduced a brief cosine fade so the *edges* of the gap are
/// smooth — but a 32-frame cosine creates a spectral peak near 750 Hz, which sounds like a
/// brief F#-ish tone every time it fires. For dense networks this can be a perceptible
/// pattern. The user picks the artifact character here.
///
/// Receiver-side only. The sender has no idea its packets came up late at the listener; each
/// listening machine decides locally what its own underruns sound like. Stored per-profile.
/// </summary>
public enum ConcealmentArtifact
{
    /// <summary>Legacy: 32-frame cosine fade-out + fade-in. ~750 Hz spectral peak — brief tone
    /// close to F#5. Was the default 2026-05-04 to 2026-05-06; removed from the dropdown after
    /// user feedback that it sounded harsh on orchestral content. Kept in the enum so old
    /// profile JSONs still parse; the dialog coerces it to NoiseBurst on load.</summary>
    CosineToneShort = 0,

    /// <summary>Legacy: 96-frame cosine fade. ~250 Hz spectral peak — softer thump than the
    /// short variant. Removed from the dropdown 2026-05-06 same as CosineToneShort. Kept for
    /// back-compat with old profile JSONs.</summary>
    CosineToneLow = 1,

    /// <summary>32-frame burst of white noise enveloped at the last sample's amplitude.
    /// Energy is broadband (no audible pitch); sounds like a brief shhh and tends to blend
    /// into music more than a tone does. The current default since 2026-05-06.</summary>
    NoiseBurst = 2,

    /// <summary>No concealment. Hard zero-fill across the gap (the pre-2026-05-04 behaviour).
    /// You'll hear the raw click at the amplitude transition — useful for direct
    /// comparison with the smoothed options, or if the click somehow bothers you less than
    /// any of the synthesised artifacts.</summary>
    Click = 3,
}
