using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace OurNotes.Director.Core;

/// <summary>Fingerprints the renderer in both managed development and native publishing.</summary>
internal static class BuildIdentity
{
    public static string Sha256()
    {
        // Native AOT folds managed assemblies into the executable; Assembly.Location
        // is empty there. Hash the actual native program when no JIT is present.
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            string executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the native executable for provenance.");
            using var stream = File.OpenRead(executable);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        // The generated module identity changes when the managed library changes.
        // It avoids hashing the dotnet host during a normal dotnet run/build.
        var assembly = typeof(BuildIdentity).Assembly;
        string identity = assembly.ManifestModule.ModuleVersionId.ToString("D") + ":" +
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Archive.Hash(Encoding.UTF8.GetBytes(identity));
    }
}
