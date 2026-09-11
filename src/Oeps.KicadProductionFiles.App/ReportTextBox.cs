using System.Text;

namespace Oeps.KicadProductionFiles.App;

/// <summary>Selectable report text with coloured status symbols for every passed or failed test.</summary>
internal sealed class ReportTextBox : RichTextBox
{
    internal static readonly Color PassedColor = Color.FromArgb(21, 128, 61);
    internal static readonly Color FailedColor = Color.FromArgb(198, 40, 40);

    internal ReportTextBox()
    {
        ReadOnly = true;
        Dock = DockStyle.Fill;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        BorderStyle = BorderStyle.None;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(32, 44, 58);
        AccessibleName = "Check report";
        WordWrap = true;
        DetectUrls = false;
        Margin = new Padding(0, 8, 0, 0);
    }

    internal void ShowReport(string title, string detail, bool? allPassed = null)
    {
        if (allPassed is bool passed)
            title = (passed ? "[PASSED] All tests passed" : "[FAILED] Not all tests passed") + "\n\n" + title;
        var text = new StringBuilder();
        var markers = new List<(int Position, Color Color)>();
        foreach (var line in (title + "\n\n" + detail).ReplaceLineEndings("\n").Split('\n'))
        {
            if (text.Length > 0) text.Append('\n');
            if (line.StartsWith("[PASSED] ", StringComparison.Ordinal))
            { markers.Add((text.Length, PassedColor)); text.Append("✓ "); }
            else if (line.StartsWith("[FAILED] ", StringComparison.Ordinal))
            { markers.Add((text.Length, FailedColor)); text.Append("✗ "); }
            text.Append(line);
        }
        Text = text.ToString();
        SelectAll();
        SelectionColor = ForeColor;
        SelectionFont = Font;
        using var symbols = new Font("Segoe UI Symbol", Font.SizeInPoints, FontStyle.Bold);
        foreach (var (position, color) in markers)
        {
            Select(position, 1);
            SelectionFont = symbols;
            SelectionColor = color;
        }
        Select(0, 0);
        ScrollToCaret();
    }
}
