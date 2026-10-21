using Microsoft.Win32;

namespace OurNotes.Director.WinUI;

internal static partial class WindowsGpuPreference
{
    internal static void RunRegressionSmoke(Action<bool, string> check)
    {
        string fixture = @"Software\OurNotes\DirectorTests\" + Guid.NewGuid().ToString("N");
        string[] paths = [Path.Combine(AppContext.BaseDirectory, "OurNotes.Director.exe"), Path.Combine(AppContext.BaseDirectory, "advkit.exe")];
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(fixture)!;
            key.SetValue(paths[0], "AutoHDREnable=1;GpuPreference=2;");
            key.SetValue(paths[1], "AutoHDREnable=1;GpuPreference=1;");
            check(!MatchesKey(key, paths, 2), "显卡保存检查包含导出引擎，检测编辑器独显但导出引擎核显的分歧");
            ApplyToKey(key, paths, 2);
            check(MatchesKey(key, paths, 2) && paths.All(path => key.GetValue(path)?.ToString() == "AutoHDREnable=1;GpuPreference=2;"),
                "保存显卡选择修复引擎的不同偏好，并保留每个程序的其他 Windows 图形选项");
            key.DeleteValue(paths[0]);
            check(!MatchesKey(key, paths, 2) && MatchesKey(key, [paths[0]], 0),
                "Windows 外部删除编辑器显卡覆盖后，当前选择正确恢复为自动");
            key.SetValue(paths[0], 42, RegistryValueKind.DWord);
            string before = key.GetValue(paths[1])!.ToString()!;
            bool rejected = false;
            try { ApplyToKey(key, paths, 1); }
            catch (IOException) { rejected = true; }
            check(rejected && (int)key.GetValue(paths[0])! == 42 && key.GetValue(paths[1])?.ToString() == before,
                "错误类型的注册表值在开始写入前被拒绝，两个程序均保留原配置");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(fixture, false); }
    }
}
