using Microsoft.Win32;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal static class WindowsGpuPreference
{
    const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    internal static int? ReadCurrentPreference()
    {
        if (Environment.ProcessPath is not { } path) return null;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key?.GetValue(path) is not string text) return null;
        var token = text.Split(';').LastOrDefault(value => value.Split('=', 2)[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase));
        return token?.Split('=', 2) is [_, var value] && int.TryParse(value, out int preference) && preference is >= 0 and <= 2 ? preference : 0;
    }
    internal static void Apply(int preference, string engine)
    {
        string[] paths = GpuPreferencePolicy.Executables(Environment.ProcessPath, engine);
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new IOException("无法保存此应用的 Windows 显卡偏好。");
        ApplyToKey(key, paths, preference);
    }
    internal static void ApplyToKey(RegistryKey key, IReadOnlyList<string> paths, int preference)
    {
        var changes = paths.Select(path => (Path: path, Value: key.GetValue(path, null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
            .Select(item => (item.Path, Old: item.Value is null or string ? (string?)item.Value
                : throw new IOException("已有显卡配置不是文本，未改写：" + item.Path)))
            .Select(item => (item.Path, item.Old, Next: GpuPreferencePolicy.Merge(item.Old, preference))).ToArray();
        try
        {
            foreach (var change in changes)
                if (change.Next is null) key.DeleteValue(change.Path, false);
                else key.SetValue(change.Path, change.Next, RegistryValueKind.String);
        }
        catch (Exception original)
        {
            var errors = new List<Exception> { original };
            foreach (var change in changes)
                try
                {
                    if (change.Old is null) key.DeleteValue(change.Path, false);
                    else key.SetValue(change.Path, change.Old, RegistryValueKind.String);
                }
                catch (Exception rollback) { errors.Add(rollback); }
            throw new AggregateException("显卡偏好保存失败；已尝试恢复原配置。", errors);
        }
    }
}
