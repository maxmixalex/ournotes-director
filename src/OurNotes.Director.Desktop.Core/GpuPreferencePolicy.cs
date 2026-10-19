namespace OurNotes.Director.Desktop.Core;

/// <summary>Windows' per-executable GPU preference, not a promise of a particular
/// active device. Preserve other application graphics options in the same value.</summary>
public static class GpuPreferencePolicy
{
    public static int Read(DesktopSettings settings) =>
        int.TryParse(settings.Forms.GetValueOrDefault("graphics")?.GetValueOrDefault("preference"), out int value)
            && value is >= 0 and <= 2 ? value : 0;
    public static void Store(DesktopSettings settings, int preference)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preference);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preference, 2);
        if (!settings.Forms.TryGetValue("graphics", out var values)) settings.Forms["graphics"] = values = new(StringComparer.Ordinal);
        values["preference"] = preference.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string? Merge(string? existing, int preference)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preference);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preference, 2);
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
}
