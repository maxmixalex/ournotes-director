using System.Numerics;
using System.Diagnostics;

namespace OurNotes.Director.Core;

public sealed unsafe partial class OffscreenGl
{
    internal double FocusPreparationMilliseconds { get; private set; }
    internal void PrepareFocusMaterials(UnityShaderLibrary shaders, float radius)
    {
        long started=Stopwatch.GetTimestamp();
        // Drivers can specialize the same shader for power-of-two samplers and
        // for framebuffers with a depth/stencil attachment, even with testing
        // disabled. A fixed 16x16 color-only draw leaves both real paths cold.
        // Keep each source and rounded-up half-size sampler in its actual class.
        static int Representative(int size) => BitOperations.IsPow2((uint)size) ? 16
            : BitOperations.IsPow2((uint)((size + 1) / 2)) ? 15 : 17;
        var sizes = new HashSet<(int Width, int Height)>();
        foreach (double scale in new[] { 1d, .5, .25 })
        {
            int width = scale == 1 ? Width : Math.Max(16, (int)Math.Round(Width * scale));
            int height = scale == 1 ? Height : Math.Max(16, (int)Math.Round(Height * scale));
            sizes.Add((Representative(width), Representative(height)));
        }
        foreach (var size in sizes)
        {
            // These private targets never hold scene pixels, captures or model
            // state. Disposal also releases their optional depth attachments.
            using var warmup = CreateDrawingCanvas(size.Width, size.Height);
            using (UseDrawingCanvas(warmup))
            {
                Begin([0, 0, 0, 0]);
                Blur(shaders, 1, radius);
                BeginLayer(); Blur(shaders, 1, radius); EndLayer();

                // Stage geometry can attach depth to the main canvas, while a
                // separated Cubism layer remains color-only. Prepare both.
                EnsureDepthStencil();
                Blur(shaders, 1, radius);
                BeginLayer(); Blur(shaders, 1, radius); EndLayer();
                _ = Read(size.Width, size.Height);
            }
        }
        FocusPreparationMilliseconds+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
