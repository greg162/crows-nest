using System.Text;
using Crowsnest.Device.Protocol;

namespace Crowsnest.DeviceSimulator;

/// <summary>
/// Draws a state frame the way the panel would, in characters. Crude on purpose: its job
/// is to prove the frame arrived intact and that the cursor span lands on the right digits,
/// not to look like the real screen.
/// </summary>
public static class ConsolePanelRenderer
{
    private const int Width = 34;

    public static string Render(HostState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var sb = new StringBuilder();
        sb.Append("    ╭").Append('─', Width).AppendLine("╮");

        Centre(sb, state.Page.Title);
        Centre(sb, $"{state.Page.Index + 1}/{state.Page.Count}   {state.Page.Layout}");
        Blank(sb);

        foreach (WireField field in state.Fields)
        {
            Left(sb, $"{field.Label}{(field.Pending ? "  •pending" : string.Empty)}");
            Centre(sb, field.Text);

            if (field.Cursor is [int start, int end] && start >= 0 && end <= field.Text.Length && end > start)
            {
                // The underline has to sit under the same columns the text was centred into,
                // which is the only part of this renderer worth getting exactly right.
                int pad = (Width - field.Text.Length) / 2;
                Raw(sb, new string(' ', pad + start) + new string('▔', end - start));
            }

            Blank(sb);
        }

        Left(sb, $"sim: {state.Sim}    rev {state.Revision}  ack {state.Ack}");
        sb.Append("    ╰").Append('─', Width).AppendLine("╯");
        return sb.ToString();
    }

    private static void Centre(StringBuilder sb, string text)
    {
        string clipped = Clip(text);
        int pad = (Width - clipped.Length) / 2;
        Raw(sb, new string(' ', pad) + clipped);
    }

    private static void Left(StringBuilder sb, string text) => Raw(sb, "  " + Clip(text, Width - 2));

    private static void Blank(StringBuilder sb) => Raw(sb, string.Empty);

    private static void Raw(StringBuilder sb, string line) =>
        sb.Append("    │").Append(Clip(line).PadRight(Width)).AppendLine("│");

    private static string Clip(string text, int max = Width) =>
        text.Length <= max ? text : text[..max];
}
