using System.Text.Json;
using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Application;

/// <summary>
/// Reads a panel module's view file (spec §5.5): which pages it shows, in which of the
/// firmware's layouts, and with which parameters. Shaped like
///
/// <code>
/// { "pages": [
///     { "id": "com1", "title": "COM1", "layout": "pair",
///       "fields": [ "com1.standby", "com1.active" ], "swapEvent": "COM_STBY_RADIO_SWAP" } ] }
/// </code>
///
/// <c>layout</c> is one of the names the firmware draws: <c>pair</c>, <c>single</c> or
/// <c>dual</c>, in lower case, the same words the wire uses (<see cref="PageLayoutNames"/>).
/// The first field is the one the knob tunes; on a <c>pair</c> page that is the standby value. <c>swapEvent</c> is optional. Comments and
/// trailing commas are allowed. Whether the fields are registered is checked by
/// <see cref="PanelEngine"/>, which has the registry.
/// </summary>
public static class PanelViewReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <exception cref="InvalidDataException">The file is malformed or a page is incomplete.</exception>
    public static IReadOnlyList<PanelPage> Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ViewDto? view;
        try
        {
            view = JsonSerializer.Deserialize<ViewDto>(json, Options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"View JSON is malformed: {e.Message}", e);
        }

        if (view?.Pages is not { Length: > 0 } pages)
        {
            throw new InvalidDataException("A view file needs a \"pages\" array with at least one page.");
        }

        return [.. pages.Select(Build)];
    }

    private static PanelPage Build(PageDto page, int position)
    {
        string id = string.IsNullOrWhiteSpace(page.Id)
            ? throw new InvalidDataException($"View page {position} has no \"id\".")
            : page.Id;

        InvalidDataException Invalid(string problem) => new($"View page '{id}': {problem}.");

        string title = string.IsNullOrWhiteSpace(page.Title) ? throw Invalid("\"title\" is required") : page.Title;

        PageLayout layout = PageLayoutNames.Parse(page.Layout)
            ?? throw Invalid($"\"layout\" is \"{page.Layout}\", which is not one of {string.Join(", ", PageLayoutNames.All)}");

        if (page.Fields is not { Length: > 0 } fields || fields.Any(string.IsNullOrWhiteSpace))
        {
            throw Invalid("\"fields\" must list at least one parameter id");
        }

        if (page.SwapEvent is { } swap && string.IsNullOrWhiteSpace(swap))
        {
            throw Invalid("\"swapEvent\" is empty; leave it out instead");
        }

        return new PanelPage(id, title, layout, [.. fields.Select(f => new ParameterId(f))], page.SwapEvent);
    }

    // Deserialisation targets. Everything nullable so a missing field becomes a named error.
    private sealed record ViewDto(PageDto[]? Pages);

    private sealed record PageDto(string? Id, string? Title, string? Layout, string[]? Fields, string? SwapEvent);
}
