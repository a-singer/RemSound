using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Recording settings dialog. Three listboxes laid out left-to-right:
///   * Recording &source (Alt+S) — what audio gets captured
///   * File &format (Alt+F)       — WAV / MP3 / Ogg / FLAC
///   * Audio &attributes (Alt+A)  — bit depth or bitrate, plus channel mode
///
/// The attributes list repopulates whenever the file-format selection changes, so the user
/// always sees only the choices that make sense for the format. Selecting a WAV-only
/// attribute then switching the format to MP3 doesn't carry forward — the format-attributes
/// list resets to a sensible default for the new format.
///
/// Settings are written back to the profile only when the user presses OK. Cancel / Esc
/// discards. The dialog also exposes <see cref="ChangedAnything"/> so the caller can
/// MarkProfileDirty after a successful OK.
///
/// Reachable from the Record menu → "Recording settings...".
/// </summary>
internal sealed class RecordingSettingsDialog : Form
{
    private readonly RecordingSettings working;  // mutated as the user interacts

    private readonly Label sourceLabel = new()
    {
        Text = "Recording &source (Alt+S):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox sourceList = new()
    {
        AccessibleName = "Recording source",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 120,
    };

    private readonly Label formatLabel = new()
    {
        Text = "File &format (Alt+F):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox formatList = new()
    {
        AccessibleName = "File format",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 120,
    };

    private readonly Label attributesLabel = new()
    {
        Text = "Audio format &attributes (Alt+A):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox attributesList = new()
    {
        AccessibleName = "Audio format attributes",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 200,
    };

    private readonly Button okButton = new()
    {
        Text = "&OK",
        AutoSize = true,
        DialogResult = DialogResult.OK,
    };

    private readonly Button cancelButton = new()
    {
        Text = "&Cancel",
        AutoSize = true,
        DialogResult = DialogResult.Cancel,
    };

    /// <summary>True if the user pressed OK and any setting actually changed. The caller
    /// uses this to mark the profile dirty.</summary>
    public bool ChangedAnything { get; private set; }

    /// <summary>The final settings (after OK). Equals the input settings if Cancel was
    /// pressed — caller should ignore this on a non-OK DialogResult.</summary>
    public RecordingSettings Result => working;

    public RecordingSettingsDialog(RecordingSettings current)
    {
        working = current?.Clone() ?? new RecordingSettings();
        var initialSnapshot = working.Clone();

        Text = "Recording settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(700, 360);

        PopulateSourceList();
        PopulateFormatList();
        PopulateAttributesList();

        SelectFromSource(working.Source);
        SelectFromFormat(working.FileFormat);
        SelectFromAttributes(working);

        sourceList.SelectedIndexChanged += (_, _) =>
        {
            if (sourceList.SelectedIndex < 0) return;
            working.Source = (RecordingSource)sourceList.SelectedIndex;
        };

        formatList.SelectedIndexChanged += (_, _) =>
        {
            if (formatList.SelectedIndex < 0) return;
            var newFormat = (RecordingFileFormat)formatList.SelectedIndex;
            if (newFormat == working.FileFormat) return;
            working.FileFormat = newFormat;
            PopulateAttributesList();
            SelectFromAttributes(working);
        };

        attributesList.SelectedIndexChanged += (_, _) =>
        {
            if (attributesList.SelectedIndex < 0) return;
            ApplyAttributesSelection();
        };

        okButton.Click += (_, _) =>
        {
            ChangedAnything = !SettingsEqual(initialSnapshot, working);
        };

        // Three columns side by side, OK/Cancel row beneath.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 2,
        };
        for (var i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var sourceColumn = MakeColumn(sourceLabel, sourceList);
        var formatColumn = MakeColumn(formatLabel, formatList);
        var attributesColumn = MakeColumn(attributesLabel, attributesList);
        grid.Controls.Add(sourceColumn, 0, 0);
        grid.SetRowSpan(sourceColumn, 2);
        grid.Controls.Add(formatColumn, 1, 0);
        grid.SetRowSpan(formatColumn, 2);
        grid.Controls.Add(attributesColumn, 2, 0);
        grid.SetRowSpan(attributesColumn, 2);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 0, 12, 12),
        };
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);

        // Order: dialog body first (grid), then buttons docked beneath.
        Controls.Add(grid);
        Controls.Add(buttonRow);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        // Tab order top-to-bottom of the visible flow: source, format, attributes, OK, Cancel.
        sourceList.TabIndex = 0;
        formatList.TabIndex = 1;
        attributesList.TabIndex = 2;
        okButton.TabIndex = 3;
        cancelButton.TabIndex = 4;
    }

    private static Control MakeColumn(Label label, ListBox list)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        list.Dock = DockStyle.Fill;
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(list, 0, 1);
        return panel;
    }

    private void PopulateSourceList()
    {
        sourceList.BeginUpdate();
        sourceList.Items.Clear();
        // Order MUST match RecordingSource enum values 0/1/2.
        sourceList.Items.Add("Record all received audio");
        sourceList.Items.Add("Record all sent audio");
        sourceList.Items.Add("Record both sent and received audio");
        sourceList.EndUpdate();
    }

    private void PopulateFormatList()
    {
        formatList.BeginUpdate();
        formatList.Items.Clear();
        // Order MUST match RecordingFileFormat enum values 0..3.
        formatList.Items.Add("WAV (uncompressed)");
        formatList.Items.Add("MP3");
        formatList.Items.Add("Ogg-Opus");
        formatList.Items.Add("FLAC (lossless)");
        formatList.EndUpdate();
    }

    // === Per-format attribute tables ===
    // All four formats currently record at the engine's 48 kHz mix rate, so labels include
    // "48 kHz" to make the sample rate explicit (it's not a choice — it's a statement of fact
    // about what gets written, which removes a common surprise for users who expected to see
    // a rate picker). Channel mode is part of every row because it determines file shape
    // alongside the format-specific quality knob.

    private static readonly (int Bits, RecordingChannelMode Mode, string Label)[] WavAttributes =
    {
        (16, RecordingChannelMode.Stereo, "16-bit PCM, 48 kHz, stereo"),
        (16, RecordingChannelMode.Mono,   "16-bit PCM, 48 kHz, mono"),
        (24, RecordingChannelMode.Stereo, "24-bit PCM, 48 kHz, stereo"),
        (24, RecordingChannelMode.Mono,   "24-bit PCM, 48 kHz, mono"),
        (32, RecordingChannelMode.Stereo, "32-bit float, 48 kHz, stereo"),
        (32, RecordingChannelMode.Mono,   "32-bit float, 48 kHz, mono"),
    };

    private static readonly (int Kbps, RecordingChannelMode Mode, string Label)[] Mp3Attributes =
    {
        (128, RecordingChannelMode.Stereo, "128 kbps, 48 kHz, stereo"),
        (128, RecordingChannelMode.Mono,   "128 kbps, 48 kHz, mono"),
        (192, RecordingChannelMode.Stereo, "192 kbps, 48 kHz, stereo"),
        (192, RecordingChannelMode.Mono,   "192 kbps, 48 kHz, mono"),
        (256, RecordingChannelMode.Stereo, "256 kbps, 48 kHz, stereo"),
        (256, RecordingChannelMode.Mono,   "256 kbps, 48 kHz, mono"),
        (320, RecordingChannelMode.Stereo, "320 kbps, 48 kHz, stereo"),
        (320, RecordingChannelMode.Mono,   "320 kbps, 48 kHz, mono"),
    };

    // OGG-Opus is VBR — kbps numbers are the encoder's target average. Opus' music-quality
    // sweet spot starts around 96 kbps; we expose 96 / 128 / 192 / 256 so users have a
    // smaller-file option without it sounding obviously lossy on dense material.
    private static readonly (int Kbps, RecordingChannelMode Mode, string Label)[] OggOpusAttributes =
    {
        (96,  RecordingChannelMode.Stereo, "96 kbps, 48 kHz, stereo"),
        (96,  RecordingChannelMode.Mono,   "96 kbps, 48 kHz, mono"),
        (128, RecordingChannelMode.Stereo, "128 kbps, 48 kHz, stereo"),
        (128, RecordingChannelMode.Mono,   "128 kbps, 48 kHz, mono"),
        (192, RecordingChannelMode.Stereo, "192 kbps, 48 kHz, stereo"),
        (192, RecordingChannelMode.Mono,   "192 kbps, 48 kHz, mono"),
        (256, RecordingChannelMode.Stereo, "256 kbps, 48 kHz, stereo"),
        (256, RecordingChannelMode.Mono,   "256 kbps, 48 kHz, mono"),
    };

    // FLAC is lossless — quality knob is just bit depth (and silently, compression level,
    // which we hard-fix at the reference encoder's default 5). 32-bit float isn't a FLAC
    // option (FLAC stores integer PCM), so it's deliberately absent.
    private static readonly (int Bits, RecordingChannelMode Mode, string Label)[] FlacAttributes =
    {
        (16, RecordingChannelMode.Stereo, "16-bit, 48 kHz, stereo"),
        (16, RecordingChannelMode.Mono,   "16-bit, 48 kHz, mono"),
        (24, RecordingChannelMode.Stereo, "24-bit, 48 kHz, stereo"),
        (24, RecordingChannelMode.Mono,   "24-bit, 48 kHz, mono"),
    };

    private void PopulateAttributesList()
    {
        attributesList.BeginUpdate();
        attributesList.Items.Clear();
        switch (working.FileFormat)
        {
            case RecordingFileFormat.Wav:
                foreach (var (_, _, label) in WavAttributes) attributesList.Items.Add(label);
                break;
            case RecordingFileFormat.Mp3:
                foreach (var (_, _, label) in Mp3Attributes) attributesList.Items.Add(label);
                break;
            case RecordingFileFormat.Ogg:
                foreach (var (_, _, label) in OggOpusAttributes) attributesList.Items.Add(label);
                break;
            case RecordingFileFormat.Flac:
                foreach (var (_, _, label) in FlacAttributes) attributesList.Items.Add(label);
                break;
            default:
                attributesList.Items.Add("Default settings");
                break;
        }
        attributesList.EndUpdate();
    }

    private void SelectFromSource(RecordingSource src)
    {
        var idx = (int)src;
        if (idx >= 0 && idx < sourceList.Items.Count) sourceList.SelectedIndex = idx;
    }

    private void SelectFromFormat(RecordingFileFormat fmt)
    {
        var idx = (int)fmt;
        if (idx >= 0 && idx < formatList.Items.Count) formatList.SelectedIndex = idx;
    }

    private void SelectFromAttributes(RecordingSettings s)
    {
        switch (s.FileFormat)
        {
            case RecordingFileFormat.Wav:
                for (var i = 0; i < WavAttributes.Length; i++)
                {
                    var (bits, mode, _) = WavAttributes[i];
                    if (bits == s.WavBitsPerSample && mode == s.ChannelMode)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 2; // 24-bit stereo default
                break;
            case RecordingFileFormat.Mp3:
                for (var i = 0; i < Mp3Attributes.Length; i++)
                {
                    var (kbps, mode, _) = Mp3Attributes[i];
                    if (kbps == s.Mp3BitrateKbps && mode == s.ChannelMode)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 6; // 320 kbps stereo default
                break;
            case RecordingFileFormat.Ogg:
                for (var i = 0; i < OggOpusAttributes.Length; i++)
                {
                    var (kbps, mode, _) = OggOpusAttributes[i];
                    if (kbps == s.OggOpusBitrateKbps && mode == s.ChannelMode)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 4; // 192 kbps stereo default
                break;
            case RecordingFileFormat.Flac:
                for (var i = 0; i < FlacAttributes.Length; i++)
                {
                    var (bits, mode, _) = FlacAttributes[i];
                    if (bits == s.FlacBitsPerSample && mode == s.ChannelMode)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 2; // 24-bit stereo default
                break;
            default:
                if (attributesList.Items.Count > 0) attributesList.SelectedIndex = 0;
                break;
        }
    }

    private void ApplyAttributesSelection()
    {
        var idx = attributesList.SelectedIndex;
        if (idx < 0) return;
        switch (working.FileFormat)
        {
            case RecordingFileFormat.Wav:
                if (idx < WavAttributes.Length)
                {
                    var (bits, mode, _) = WavAttributes[idx];
                    working.WavBitsPerSample = bits;
                    working.ChannelMode = mode;
                }
                break;
            case RecordingFileFormat.Mp3:
                if (idx < Mp3Attributes.Length)
                {
                    var (kbps, mode, _) = Mp3Attributes[idx];
                    working.Mp3BitrateKbps = kbps;
                    working.ChannelMode = mode;
                }
                break;
            case RecordingFileFormat.Ogg:
                if (idx < OggOpusAttributes.Length)
                {
                    var (kbps, mode, _) = OggOpusAttributes[idx];
                    working.OggOpusBitrateKbps = kbps;
                    working.ChannelMode = mode;
                }
                break;
            case RecordingFileFormat.Flac:
                if (idx < FlacAttributes.Length)
                {
                    var (bits, mode, _) = FlacAttributes[idx];
                    working.FlacBitsPerSample = bits;
                    working.ChannelMode = mode;
                }
                break;
            default:
                break;
        }
    }

    private static bool SettingsEqual(RecordingSettings a, RecordingSettings b) =>
        a.Source == b.Source
        && a.FileFormat == b.FileFormat
        && a.ChannelMode == b.ChannelMode
        && a.WavBitsPerSample == b.WavBitsPerSample
        && a.Mp3BitrateKbps == b.Mp3BitrateKbps
        && a.OggOpusBitrateKbps == b.OggOpusBitrateKbps
        && a.FlacBitsPerSample == b.FlacBitsPerSample
        && a.FlacCompressionLevel == b.FlacCompressionLevel
        && string.Equals(a.Folder ?? string.Empty, b.Folder ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
