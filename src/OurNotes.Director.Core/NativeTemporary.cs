namespace OurNotes.Director.Core;

// Seekable media inputs stay beside this portable tool unless an explicit scratch root is supplied.
internal static class NativeTemporary
{
    static readonly string Root=Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("OURNOTES_TEMP")??AppContext.BaseDirectory,".ournotes-temp"));
    public static string Create(string kind,string extension)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root,"ournotes-"+kind+"-"+Guid.NewGuid().ToString("N")+extension);
    }
    public static void Delete(string path)
    {
        string resolved=Path.GetFullPath(path);
        if(!resolved.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Media cleanup outside scratch directory");
        if(File.Exists(resolved))File.Delete(resolved);
        // Non-recursive; another decoder may still own files in this directory.
        try{if(Directory.Exists(Root)&&!Directory.EnumerateFileSystemEntries(Root).Any())Directory.Delete(Root);}catch(IOException){}
    }
}
