using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class GpuPreferenceTests
{
    public static void Run(Action<bool, string> check)
    {
        check(GpuPreferencePolicy.FromRegistryValue(null) == 0
            && GpuPreferencePolicy.FromRegistryValue("AutoHDREnable=1;Foo=x;") == 0,
            "Removing a Windows GPU override returns to automatic instead of resurrecting the application's saved preference");
        check(GpuPreferencePolicy.FromRegistryValue("GpuPreference=2; gpupreference = 1 ;") == 1
            && GpuPreferencePolicy.FromRegistryValue("GpuPreference=1;GpuPreference=2;") == 2,
            "GPU registry parsing follows the last complete token, including Windows option whitespace and case");
        check(new[] { "GpuPreference=3;", "GpuPreference=-1;", "GpuPreference=2;GpuPreference=bad;", "NotGpuPreference=2;" }
            .All(text => GpuPreferencePolicy.FromRegistryValue(text) == 0),
            "Malformed or unrelated GPU registry values never select an unsupported device policy");
        check(!GpuPreferencePolicy.MatchesAll(["GpuPreference=2;", "GpuPreference=1;"], 2)
            && !GpuPreferencePolicy.MatchesAll(["GpuPreference=2;", null], 2),
            "Saving a matching editor GPU still repairs a different or missing export-engine GPU preference");
        check(GpuPreferencePolicy.MatchesAll(["AutoHDREnable=1;GpuPreference=2;", "GpuPreference=2;"], 2)
            && GpuPreferencePolicy.MatchesAll([null, "AutoHDREnable=1;"], 0),
            "Already matching editor and engine graphics policies are idempotent without removing unrelated Windows options");
        string? merged = GpuPreferencePolicy.Merge("GpuPreference=1;AutoHDREnable=1;gpupreference=2;Foo=a=b;", 1);
        check(merged == "AutoHDREnable=1;Foo=a=b;GpuPreference=1;"
            && GpuPreferencePolicy.FromRegistryValue(merged) == 1,
            "Writing a GPU preference removes duplicate overrides while preserving equals signs in other graphics values");
        string directory = Path.Combine(Path.GetTempPath(), "ournotes-gpu-policy-fixture");
        string application = Path.Combine(directory, "OurNotes.Director.exe"), engine = Path.Combine(directory, "advkit.exe");
        check(GpuPreferencePolicy.Executables(application, engine).SequenceEqual([application, engine])
            && GpuPreferencePolicy.Executables(application, application).SequenceEqual([application])
            && GpuPreferencePolicy.Executables(Path.Combine(directory, "DOTNET.EXE"), engine).SequenceEqual([engine]),
            "GPU policies target both actual apphosts, deduplicate one executable, and leave the shared dotnet host untouched");
    }
}
