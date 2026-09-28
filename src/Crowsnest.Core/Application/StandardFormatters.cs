using Crowsnest.Core.Domain.Formatting;

namespace Crowsnest.Core.Application;

/// <summary>The formatters in <c>Domain/Formatting/</c>, by the key an entry's <c>"format"</c> names (spec §5.2).</summary>
public static class StandardFormatters
{
    public static IReadOnlyDictionary<string, IValueFormatter> All { get; } = new Dictionary<string, IValueFormatter>
    {
        ["freq3"] = new FrequencyFormatter(decimals: 3),
        ["freq2"] = new FrequencyFormatter(decimals: 2),
        ["thousands"] = new GroupedFormatter(),
        ["signedThousands"] = new GroupedFormatter(signed: true),
        ["deg3"] = new PaddedFormatter(digits: 3),
        ["code4"] = new PaddedFormatter(digits: 4),
    };
}
