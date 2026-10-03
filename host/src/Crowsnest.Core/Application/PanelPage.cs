using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Application;

/// <summary>
/// One screen's worth of parameters (spec §5.5). The first field is the one the knob tunes;
/// on an <see cref="PageLayout.ActiveStandbyPair"/> page that is the standby value, and
/// <paramref name="SwapEvent"/> exchanges the two.
/// </summary>
public sealed record PanelPage(
    string Id,
    string Title,
    PageLayout Layout,
    IReadOnlyList<ParameterId> Fields,
    string? SwapEvent = null);

/// <summary>Where a device is in its list of pages. Next and previous wrap round.</summary>
public sealed class PageNavigator
{
    private readonly IReadOnlyList<PanelPage> _pages;

    public PageNavigator(IReadOnlyList<PanelPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        if (pages.Count == 0)
        {
            throw new ArgumentException("A device needs at least one page to show.", nameof(pages));
        }

        if (pages.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != pages.Count)
        {
            throw new ArgumentException("Two pages share an id.", nameof(pages));
        }

        _pages = pages;
    }

    public PanelPage Current => _pages[Index];

    public int Index { get; private set; }

    public int Count => _pages.Count;

    public void Next() => Index = (Index + 1) % _pages.Count;

    public void Previous() => Index = (Index - 1 + _pages.Count) % _pages.Count;

    public bool TryGoTo(string pageId)
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            if (string.Equals(_pages[i].Id, pageId, StringComparison.Ordinal))
            {
                Index = i;
                return true;
            }
        }

        return false;
    }
}
