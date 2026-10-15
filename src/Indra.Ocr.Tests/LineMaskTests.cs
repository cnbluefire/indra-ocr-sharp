using System.Text.Json;
using Indra.Ocr.Imaging;
using Indra.Ocr.Rec;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for the CenterMask port — <see cref="LineMask.WindowBox"/>,
/// <see cref="LineMask.MaskWindows"/>, <see cref="LineMask.MaskedWindows"/> and
/// <see cref="LineMask.CropLines"/> (<c>indra_ocr/line_mask.py</c>, see
/// <c>docs/085-line-mask.md</c>).
/// </summary>
/// <remarks>
/// The shipped model set has no <c>mask.onnx</c>, so the fixture drives the reference with a
/// deterministic stand-in model (see <c>gen_line_mask_fixtures.py</c>) that both sides
/// implement identically: everything around the model — batching, padding, the float32
/// bilinear upscale of the probabilities, the 0.5 threshold, the paper-level percentile and the
/// whitening — is the real code path.
/// </remarks>
public class LineMaskTests
{
    private static readonly JsonElement Fx = Fixtures.Load("line_mask.json");

    /// <summary>The fixture's stand-in for <c>mask.onnx</c>.</summary>
    private sealed class StandIn : IMaskModel
    {
        private readonly string _kind;

        public StandIn(string kind) => _kind = kind;

        public void Run(ReadOnlySpan<float> x, int batch, int width, Span<float> p)
        {
            for (int i = 0; i < p.Length; i++)
                p[i] = _kind switch
                {
                    "flip" => 1f - x[i],
                    "parabola" => (4f * x[i]) * (1f - x[i]),
                    _ => throw new InvalidOperationException($"unknown stand-in '{_kind}'"),
                };
        }
    }

    [Fact]
    public void Window_box_clips_to_the_page_and_uses_the_box_height()
    {
        var result = new double[4];

        // h = 10: half a height to the sides, a full height above and below.
        LineMask.WindowBox(new[] { 10f, 20f, 50f, 30f }, 100.0, 80.0, result);
        Assert.Equal(new[] { 5.0, 10.0, 55.0, 40.0 }, result);

        // Clipped at all four page edges.
        LineMask.WindowBox(new[] { -4f, -6f, 104f, 86f }, 100.0, 80.0, result);
        Assert.Equal(new[] { 0.0, 0.0, 100.0, 80.0 }, result);

        // Degenerate box (y1 < y0): the height floors at 1.0, so the padding is half a pixel
        // to the sides and one pixel above and below.
        LineMask.WindowBox(new[] { 10f, 20f, 50f, 12f }, 100.0, 80.0, result);
        Assert.Equal(new[] { 9.5, 19.0, 50.5, 13.0 }, result);
    }

    [Fact]
    public void Mask_windows_match_the_reference()
    {
        int cases = 0, lines = 0;
        foreach (var c in Fx.GetProperty("cases").EnumerateArray())
        {
            var boxes = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            var model = new StandIn(c.GetProperty("model").GetString()!);
            var (gray, pw, ph) = Page(c);
            var page = new GrayImage(pw, ph, gray);

            // The keep masks, at the resolution of the (clipped) window.
            var expectedSizes = c.GetProperty("keep_sizes").EnumerateArray().ToArray();
            var expectedKeeps = c.GetProperty("keeps").EnumerateArray().ToArray();
            var keeps = LineMask.MaskWindows(model, page, boxes);
            Assert.Equal(expectedKeeps.Length, keeps.Length);

            for (int i = 0; i < keeps.Length; i++)
            {
                int wd = expectedSizes[i][0].GetInt32(), h = expectedSizes[i][1].GetInt32();
                LineMask.WindowRect(page, boxes.AsSpan(i * 4, 4),
                                    out int x0, out int y0, out int x1, out int y1);
                Assert.Equal(wd, Math.Max(0, x1 - x0));
                Assert.Equal(h, Math.Max(0, y1 - y0));

                var expected = B64.ZBytes(expectedKeeps[i].GetString()!);
                Assert.Equal(expected.Length, keeps[i].Length);
                for (int q = 0; q < expected.Length; q++)
                    Assert.Equal(expected[q] != 0, keeps[i][q]);
            }

            // The masked windows: the window with foreign ink whitened, and the box inside it.
            var expectedWindows = c.GetProperty("windows").EnumerateArray().ToArray();
            var windows = LineMask.MaskedWindows(model, page, boxes);
            Assert.Equal(expectedWindows.Length, windows.Length);
            for (int i = 0; i < windows.Length; i++)
            {
                var e = expectedWindows[i];
                Assert.Equal(e.GetProperty("x").GetInt32(), windows[i].X);
                Assert.Equal(e.GetProperty("y").GetInt32(), windows[i].Y);
                Assert.Equal(e.GetProperty("w").GetInt32(), windows[i].Width);
                Assert.Equal(e.GetProperty("h").GetInt32(), windows[i].Height);
                Assert.Equal(B64.ZBytes(e.GetProperty("pixels").GetString()!), windows[i].Pixels);
                var box = Fixtures.DoubleArray(e.GetProperty("box"));
                Assert.Equal(box.Length, windows[i].Box.Length);
                for (int k = 0; k < box.Length; k++)
                    Assert.True(box[k] == windows[i].Box[k],
                                $"box[{k}] = {windows[i].Box[k]:R}, expected {box[k]:R}");
            }

            // ... and the recognizer inputs built from them.
            int height = c.GetProperty("height").GetInt32();
            var expectedLines = c.GetProperty("lines").EnumerateArray().ToArray();
            var crops = LineMask.CropLines(page, boxes, height, model);
            Assert.Equal(expectedLines.Length, crops.Count);
            for (int i = 0; i < crops.Count; i++)
            {
                int wd = expectedLines[i].GetProperty("w").GetInt32();
                Assert.Equal(wd, crops[i].Length / height);
                var ink = B64.ZFloats(expectedLines[i].GetProperty("ink").GetString()!);
                Assert.Equal(ink.Length, crops[i].Length);
                for (int q = 0; q < ink.Length; q++)
                    Assert.True(ink[q] == crops[i][q],
                                $"line {i}[{q}] = {crops[i][q]:R}, expected {ink[q]:R}");
                lines++;
            }
            cases++;
        }
        Assert.True(cases >= 4, $"only {cases} window cases");
        Assert.True(lines >= 15, $"only {lines} lines");
    }

    [Fact]
    public void Mask_windows_handles_a_window_the_page_clip_collapsed()
    {
        // A box entirely off the page: the page clip collapses its window, and the reference
        // raises in cv2.resize (resizing an empty array). The port degrades to "nothing is
        // kept" plus an empty window, which crop_line turns into a plain white crop.
        var gray = new byte[32 * 32];
        Array.Fill(gray, (byte)200);
        var page = new GrayImage(32, 32, gray);
        var box = new[] { -80f, -80f, -40f, -60f };

        var keeps = LineMask.MaskWindows(new StandIn("flip"), page, box);
        Assert.Single(keeps);
        Assert.Empty(keeps[0]);

        var windows = LineMask.MaskedWindows(new StandIn("flip"), page, box);
        Assert.Single(windows);
        Assert.Empty(windows[0].Pixels);
        Assert.Equal(0, windows[0].Width);
        Assert.Equal(0, windows[0].Height);

        var crops = LineMask.CropLines(page, box, 32, new StandIn("flip"));
        Assert.Single(crops);
        Assert.True(crops[0].Length >= 32 * 8, "crop_line still clamps the width to 8");
        foreach (float v in crops[0]) Assert.Equal(0f, v);   // 1 - 255/255: paper
    }

    private static (byte[] Gray, int Width, int Height) Page(JsonElement c)
    {
        var page = Fx.GetProperty("pages").GetProperty(c.GetProperty("page").GetString()!);
        return (B64.ZBytes(page.GetProperty("gray").GetString()!),
                page.GetProperty("w").GetInt32(),
                page.GetProperty("h").GetInt32());
    }
}
