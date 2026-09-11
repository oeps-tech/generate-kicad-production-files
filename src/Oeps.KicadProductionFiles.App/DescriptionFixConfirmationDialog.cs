using Oeps.KicadProductionFiles.Core.ConfigureFiles;

namespace Oeps.KicadProductionFiles.App;

/// <summary>Keeps long description proposals scrollable while Yes/No remain visible.</summary>
internal sealed class DescriptionFixConfirmationDialog : Form
{
    internal RichTextBox Details { get; }
    internal Button YesButton { get; }
    internal Button NoButton { get; }

    internal DescriptionFixConfirmationDialog(ConfigurationFixPrompt prompt, Form owner)
    {
        Text = "Configure files — OEPS Description";
        Font = owner.Font;
        Icon = owner.Icon;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 500);
        MinimumSize = new Size(380, 280);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Color.FromArgb(246, 248, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Review the proposed description changes. Apply this fix?", AutoSize = true, Margin = new Padding(0, 0, 0, 12) }, 0, 0);
        Details = new RichTextBox
        {
            ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, Margin = new Padding(0),
            AccessibleName = "Proposed description changes",
            Text = prompt.Description + "\n\nCurrent check failures\n" + prompt.Failure.Detail
        };
        layout.Controls.Add(Details, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = true, Margin = new Padding(0, 12, 0, 0) };
        Button Choice(string text, DialogResult result) => new()
        {
            Text = text, DialogResult = result, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 6, 14, 6), FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(0, 103, 192), BackColor = Color.White, Margin = new Padding(8, 0, 0, 0)
        };
        NoButton = Choice("&No", DialogResult.No);
        YesButton = Choice("&Yes, update descriptions", DialogResult.Yes);
        buttons.Controls.Add(NoButton);
        buttons.Controls.Add(YesButton);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = NoButton;
        CancelButton = NoButton;
        Shown += (_, _) =>
        {
            var available = Screen.FromControl(owner).WorkingArea;
            Size = new Size(Math.Min(Width, available.Width - 24), Math.Min(Height, available.Height - 24));
            Details.Select(0, 0);
            Details.ScrollToCaret();
            NoButton.Select();
        };
    }
}
