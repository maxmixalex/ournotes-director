using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class WorkspaceLayoutSettingsTests
{
    internal static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ournotes-layout-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "settings.json");
        try
        {
            var settings = new DesktopSettings();
            check(settings.Theme == "Dark" && WorkspaceLayoutSettings.DefaultFor("live2d").TopLeft > .5
                && WorkspaceLayoutSettings.DefaultFor("scenes").TopLeft < .5,
                "New editors default to a dark workspace, with the large monitor on the correct side in each layout");
            settings.Theme = "Light";
            settings.WorkspaceLayouts["scenes"] = new(.35, .25, .65);
            settings.WorkspaceLayouts["home"] = new(.29, .32, .58);
            settings.WorkspaceLayouts["live2d"] = new(.7, .28, 1);
            settings.Save(path);
            var loaded = DesktopSettings.Load(path);
            check(loaded.Theme == "Light" && loaded.WorkspaceLayouts.Count == 3
                && settings.WorkspaceLayouts.All(pair => loaded.WorkspaceLayouts[pair.Key] == pair.Value),
                "All three workspaces round-trip independent splitter positions and preserve the chosen light theme");
            loaded.WorkspaceLayouts["scenes"] = WorkspaceLayoutSettings.DefaultFor("scenes"); loaded.Save(path);
            var reset = DesktopSettings.Load(path);
            check(reset.WorkspaceLayouts["scenes"] == WorkspaceLayoutSettings.DefaultFor("scenes")
                && reset.WorkspaceLayouts["home"] == settings.WorkspaceLayouts["home"]
                && reset.WorkspaceLayouts["live2d"] == settings.WorkspaceLayouts["live2d"],
                "Restoring one workspace does not overwrite the other editors' splitters");
            File.WriteAllText(path, """{"theme":"Default","workspaceLayouts":{"scenes":{"topLeft":-4,"bottomLeft":"bad","topHeight":7},"live2d":{"topLeft":0.75}}}""");
            var legacy = DesktopSettings.Load(path);
            check(legacy.Theme == "Default" && legacy.LoadWarning is null
                && legacy.WorkspaceLayouts["scenes"] == new WorkspaceLayoutSettings(.16, .28, .84)
                && legacy.WorkspaceLayouts["live2d"] == new WorkspaceLayoutSettings(.75, .28, 1),
                "Legacy system theme remains intact and incomplete or invalid splitters recover without losing the profile");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
