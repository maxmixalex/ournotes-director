using System.Runtime.InteropServices;
using System.Text;

namespace OurNotes.Director.Core;

public sealed partial class GpuPreviewSurface
{
    static int adapterPreference;
    /// <summary>Configure before creating any render sessions. This only orders
    /// compatible presentation adapters; WGL's rendering GPU is selected by the
    /// Windows executable policy at process launch and is reported separately.</summary>
    public static void ConfigureAdapterPreference(int preference)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preference);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preference, 2);
        Volatile.Write(ref adapterPreference, preference);
    }
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
    public bool? IsSoftwareAdapter
    {
        get
        {
            lock (gate)
            {
                if (handle == 0) return null;
                return OurNotesGpuAdapterSoftware(handle, out int software) >= 0 ? software != 0 : null;
            }
        }
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int OurNotesGpuAdapterName(nint surface, StringBuilder name, uint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int OurNotesGpuPreferredAdapter(uint preference, StringBuilder name, uint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int OurNotesGpuAdapterSoftware(nint surface, out int software);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int OurNotesGpuCreatePreferred(uint width, uint height, uint preference, out nint surface);
}
