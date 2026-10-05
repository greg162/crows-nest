using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Panels.Com;

/// <summary>
/// Keeps each COM grid on the aircraft's spacing mode (spec §5.3): the <c>comChannel</c> grid
/// type, and the behaviour that rebuilds those grids when <c>COM SPACING MODE</c> changes.
///
/// The two are one object because the grid's JSON says what it follows: a grid with
/// <c>"spacingFrom": "com1.spacing"</c> is rebuilt whenever the watch <c>com1.spacing</c>
/// reports. So use one instance per registry load: <see cref="ComPanelModule"/> creates one in
/// each <c>Configure</c>, and the composer reads the JSON with <see cref="GridFactory"/>, then
/// calls <see cref="Validate"/> on the finished registry.
/// </summary>
public sealed class ComSpacingBehaviour : IPanelBehaviour
{
    public const string GridType = "comChannel";

    // COM SPACING MODE (Enum): 0 = 25 kHz, 1 = 8.33 kHz.
    private const int TwentyFiveKhzMode = 0;
    private const int EightPointThreeThreeMode = 1;

    private readonly Dictionary<ParameterId, List<ParameterId>> _followers = [];

    /// <summary>
    /// Builds a 25 kHz grid, whatever the aircraft supports, and notes what it follows. Until
    /// the spacing watch reports, 25 kHz is the safe guess: every 25 kHz channel is legal under
    /// 8.33, so nothing written can be illegal, and all three aircraft in the matrix settled
    /// on it.
    /// </summary>
    public GridFactory GridFactory => spec =>
    {
        if (spec.OptionalString("spacingFrom") is { } from)
        {
            ParameterId watch = new(from);
            if (!_followers.TryGetValue(watch, out List<ParameterId>? followers))
            {
                _followers[watch] = followers = [];
            }

            followers.Add(spec.Parameter);
        }

        return new ComChannelGrid(
            ChannelSpacing.TwentyFiveKhz,
            spec.OptionalInt("min") ?? ComChannelGrid.BandMinKhz,
            spec.OptionalInt("max") ?? ComChannelGrid.BandMaxKhz);
    };

    /// <summary>Checks every <c>spacingFrom</c> names a watch the registry has, so a typo fails at startup.</summary>
    /// <exception cref="InvalidDataException">A grid follows a watch that does not exist.</exception>
    public void Validate(ParameterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach ((ParameterId watch, List<ParameterId> followers) in _followers)
        {
            if (!registry.Watches.Any(w => w.Id == watch))
            {
                throw new InvalidDataException($"Parameter '{followers[0]}': \"spacingFrom\" names '{watch}', which is not a registered watch.");
            }
        }
    }

    public void OnSnapshot(ParameterSnapshot snapshot, IPanelContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        if (!snapshot.Available || !_followers.TryGetValue(snapshot.Id, out List<ParameterId>? followers))
        {
            return;
        }

        ChannelSpacing? spacing = snapshot.CanonicalValue switch
        {
            TwentyFiveKhzMode => ChannelSpacing.TwentyFiveKhz,
            EightPointThreeThreeMode => ChannelSpacing.EightPointThreeThree,
            _ => null, // not a mode we know; keep the grid we have
        };

        if (spacing is not { } mode)
        {
            return;
        }

        foreach (ParameterId id in followers)
        {
            ComChannelGrid current = (ComChannelGrid)context.Parameter(id).Grid;
            if (current.Spacing != mode)
            {
                context.ReplaceGrid(id, new ComChannelGrid(mode, current.Min, current.Max));
            }
        }
    }
}
