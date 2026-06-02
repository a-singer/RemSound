namespace RemSound.App;

/// <summary>
/// Small dialog for viewing and changing the active profile's encryption password. Shows the
/// current password in a PLAIN (readable) edit box, not a masked one, on purpose: this audience
/// uses a screen reader, and a masked password field reads as a run of bullets, which is
/// useless. Letting NVDA read the actual characters is far more usable, and the security model
/// already accepts that the password is recoverable from the profile file anyway.
///
/// Returns the new password on OK (which may be empty — that clears the password), or null on
/// Cancel. The result is trimmed so an invisible trailing space can't cause a maddening
/// "we typed the same password but it won't connect" mismatch between two peers. 2026-05-31.
/// </summary>
internal static class ProfilePasswordDialog
{
    public static string? Show(IWin32Window owner, string profileTitle, string currentPassword)
    {
        using var dialog = new Form
        {
            Text = "Change profile password",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 150),
            AccessibleName = "Change profile password",
        };

        var label = new Label
        {
            Text = $"Password for profile “{profileTitle}”:",
            AutoSize = true,
        };
        var textBox = new TextBox
        {
            Text = currentPassword,
            Dock = DockStyle.Top,
            Width = 400,
            AccessibleName = $"Password for profile {profileTitle}",
        };
        var hint = new Label
        {
            Text = "Both you and the person you're connecting to must use the same password.",
            AutoSize = true,
        };

        var okButton = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        textBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 4, ColumnCount = 1 };
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(textBox, 0, 1);
        panel.Controls.Add(hint, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        panel.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(panel);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text.Trim() : null;
    }
}
