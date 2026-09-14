namespace Oeps.KicadProductionFiles.App;

internal sealed class TestDescriptionsDialog : Form
{
    internal TestDescriptionsDialog(Form owner)
    {
        Text = "Test descriptions";
        Font = owner.Font;
        Icon = owner.Icon;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 540);
        MinimumSize = new Size(380, 280);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Color.FromArgb(246, 248, 250);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        AddTab(tabs, "Check files configuration", TestDescriptions.Configuration);
        AddTab(tabs, "Generate production files", TestDescriptions.Production);
        layout.Controls.Add(tabs, 0, 0);
        var close = new Button { Text = "&Close", DialogResult = DialogResult.OK, AutoSize = true,
            Anchor = AnchorStyles.Right, Padding = new Padding(14, 6, 14, 6), Margin = new Padding(0, 10, 0, 0) };
        layout.Controls.Add(close, 0, 1);
        Controls.Add(layout);
        AcceptButton = close;
        CancelButton = close;
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(owner).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
        };
    }

    private void AddTab(TabControl tabs, string title, IReadOnlyList<TestDescription> descriptions)
    {
        var page = new TabPage(title) { Padding = new Padding(10) };
        var text = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Font,
            BackColor = Color.White, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false, AccessibleName = title + " test descriptions" };
        using var bold = new Font(Font, FontStyle.Bold);
        foreach (var description in descriptions)
        {
            var headingStart = text.TextLength;
            text.AppendText(description.Name + "\n");
            text.AppendText(description.Detail + "\n\nFix available: " + description.Fix + "\n\n");
            text.Select(headingStart, description.Name.Length);
            text.SelectionFont = bold;
            text.Select(text.TextLength, 0);
            text.SelectionFont = Font;
        }
        text.Select(0, 0);
        text.ScrollToCaret();
        page.Controls.Add(text);
        tabs.TabPages.Add(page);
    }
}
