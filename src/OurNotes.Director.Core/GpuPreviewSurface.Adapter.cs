using System.Runtime.InteropServices;
using System.Text;

namespace OurNotes.Director.Core;

public sealed partial class GpuPreviewSurface
{
    string? adapterName;
    public string AdapterName
    {
        get
        {
            lock (gate)
            {
                if (adapterName is not null) return adapterName;
                var text = new StringBuilder(256);
                try { if (handle != 0 && OurNotesGpuAdapterName(handle, text, 256) >= 0) return adapterName = text.ToString(); }
                catch (EntryPointNotFoundException) { }
                return "未取得 D3D11 设备名称";
            }
        }
    }
    public static string? PreferredAdapterName(int preference)
    {
        if (!OperatingSystem.IsWindows() || preference is < 0 or > 2) return null;
        try
        {
            var text = new StringBuilder(256);
            return OurNotesGpuPreferredAdapter((uint)preference, text, 256) >= 0 ? text.ToString() : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int OurNotesGpuAdapterName(nint surface, StringBuilder name, uint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int OurNotesGpuPreferredAdapter(uint preference, StringBuilder name, uint capacity);
}
