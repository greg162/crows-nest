using Crowsnest.Core.Domain.Formatting;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Domain;

/// <summary>
/// Everything about one tunable parameter that does not change at runtime (spec §5.2).
/// Built from the registry's JSON by <c>ParameterJsonReader</c>.
/// </summary>
public sealed record ParameterDefinition
{
    public ParameterDefinition(
        ParameterId id,
        string label,
        string groupId,
        CanonicalUnit unit,
        IValueGrid grid,
        IReadOnlyList<CursorLevel> cursors,
        ReadBinding read,
        WriteBinding write,
        IValueFormatter formatter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(formatter);

        if (cursors.Count == 0)
        {
            throw new ArgumentException($"Parameter '{id}' has no cursors, so a knob could not move it.", nameof(cursors));
        }

        Id = id;
        Label = label;
        GroupId = groupId;
        Unit = unit;
        Grid = grid;
        Cursors = cursors;
        Read = read;
        Write = write;
        Formatter = formatter;
    }

    public ParameterId Id { get; }

    /// <summary>What the panel shows above the value: <c>"COM 1 STBY"</c>.</summary>
    public string Label { get; }

    /// <summary>Binds an active/standby pair together: <c>"com1"</c>.</summary>
    public string GroupId { get; }

    public CanonicalUnit Unit { get; }

    /// <summary>
    /// The legal values. <c>init</c> so a panel behaviour can swap it at runtime with <c>with</c>,
    /// as COM does when the spacing mode changes (spec §5.8).
    /// </summary>
    public IValueGrid Grid { get; init; }

    /// <summary>Ordered coarse to fine. The first is where the cursor starts.</summary>
    public IReadOnlyList<CursorLevel> Cursors { get; }

    public ReadBinding Read { get; }

    public WriteBinding Write { get; }

    public IValueFormatter Formatter { get; }
}
