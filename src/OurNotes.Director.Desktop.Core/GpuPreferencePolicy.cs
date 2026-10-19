namespace OurNotes.Director.Desktop.Core;

/// <summary>Windows' per-executable GPU preference, not a promise of a particular
/// active device. Preserve other application graphics options in the same value.</summary>
public static class GpuPreferencePolicy
{
    /// <summary>A missing per-executable override means Windows chooses. It must
    /// not resurrect an old application setting after the user removes it in
    /// Windows Graphics Settings.</summary>
    public static int FromRegistryValue(string? text)
    {
        var token = (text ?? "").Split(';').LastOrDefault(part =>
            part.Split('=', 2)[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase));
        return token?.Split('=', 2) is [_, var value] && int.TryParse(value,
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int preference)
            && preference is >= 0 and <= 2 ? preference : 0;
    }
    public static bool MatchesAll(IEnumerable<string?> registryValues, int preference)
    {
        Validate(preference);
        return registryValues.All(value => FromRegistryValue(value) == preference);
    }
    public static int Read(DesktopSettings settings) =>
        int.TryParse(settings.Forms.GetValueOrDefault("graphics")?.GetValueOrDefault("preference"), out int value)
            && value is >= 0 and <= 2 ? value : 0;
    public static void Store(DesktopSettings settings, int preference)
    {
        Validate(preference);
        if (!settings.Forms.TryGetValue("graphics", out var values)) settings.Forms["graphics"] = values = new(StringComparer.Ordinal);
        values["preference"] = preference.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string? Merge(string? existing, int preference)
    {
        Validate(preference);
        var parts = (existing ?? "").Split(';').Where(part => !string.IsNullOrWhiteSpace(part)
            && !part.Split('=', 2)[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)).ToList();
        if (preference != 0) parts.Add("GpuPreference=" + preference);
        return parts.Count == 0 ? null : string.Join(';', parts) + ";";
    }
    public static string[] Executables(string? application, string engine)
    {
        // Do not change GPU policy globally for the shared dotnet host used by
        // source builds. Packaged apphosts and the selected export engine only.
        return new[] { application, engine }.Where(path => !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    static void Validate(int preference)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preference);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preference, 2);
    }
}
