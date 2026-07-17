namespace PalworldMonitorLauncher;

/// <summary>OK / Cancel / Don't bother me again for post-launch GPU mismatch.</summary>
internal sealed class GpuMismatchDialog : Form
{
    public enum Choice { Ok, Cancel, DontBother }

    public Choice ResultChoice { get; private set; } = Choice.Ok;

    public GpuMismatchDialog(string actualGpu, string expectedGpu)
    {
        Text = "GPU mismatch";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 200);
        BackColor = Color.FromArgb(0x16, 0x12, 0x1C);
        ForeColor = Color.FromArgb(0xF5, 0xE6, 0xC8);
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = false;

        var msg = new Label
        {
            Text =
                $"Palworld is rendering on:\n  {actualGpu}\n\n" +
                $"Your chosen display is driven by:\n  {expectedGpu}\n\n" +
                "Set Palworld to High performance in Windows Graphics settings if you need that GPU.",
            Location = new Point(16, 14),
            Size = new Size(428, 120),
        };

        var dont = MakeButton("Don't bother me again", 250);
        dont.Width = 194;
        // DialogResult alone closes a modal; calling Close() too throws on dispose (empty MessageBoxes).
        dont.Click += (_, _) => { ResultChoice = Choice.DontBother; DialogResult = DialogResult.OK; };

        var ok = MakeButton("OK", 16);
        ok.Width = 100;
        ok.Click += (_, _) => { ResultChoice = Choice.Ok; DialogResult = DialogResult.OK; };

        var cancel = MakeButton("Cancel", 128);
        cancel.Width = 100;
        cancel.Click += (_, _) => { ResultChoice = Choice.Cancel; DialogResult = DialogResult.Cancel; };


        Controls.Add(msg);
        Controls.Add(ok);
        Controls.Add(cancel);
        Controls.Add(dont);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static Button MakeButton(string text, int x) => new()
    {
        Text = text,
        Location = new Point(x, 150),
        Size = new Size(140, 32),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(0x7B, 0x3F, 0xA0),
        ForeColor = Color.FromArgb(0xF5, 0xE6, 0xC8),
    };
}
