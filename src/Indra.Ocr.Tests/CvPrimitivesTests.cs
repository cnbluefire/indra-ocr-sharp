using System.Numerics;
using Indra.Ocr.Imaging;
using Xunit.Abstractions;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for the ported OpenCV primitives.
/// </summary>
/// <remarks>
/// The fixtures were produced by the Python reference (opencv-python-headless 5.0.0) on an
/// AVX2 machine, where OpenCV dispatches the FMA kernels with a 16-column warp unroll. Every
/// comparison below is <b>exact</b>: a one-ulp difference is a failure (see
/// <c>docs/040-opencv-primitives.md</c>).
/// </remarks>
public class CvPrimitivesTests
{
    private readonly ITestOutputHelper _output;

    public CvPrimitivesTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Simd_dispatch_matches_the_reference_machine()
    {
        // OpenCV unrolls the warp inner loop `uf = vlanes_32 * 2` wide and contracts to FMA on
        // AVX2/AVX-512. The fixtures are pinned to uf = 16 (AVX2); on an AVX-512 machine the
        // port would legitimately produce different last bits and the fixtures would have to
        // be regenerated there.
        Assert.Equal(8, Vector<float>.Count);
        Assert.Equal(16, CvMath.WarpUnroll);
        Assert.True(CvMath.WarpUsesFma, "the reference kernels contract to FMA");
    }

    [Fact]
    public void Rotation_matrix_matches_cv2()
    {
        var root = Fixtures.Load("cv_rotation.json");
        int cases = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var expected = B64.Doubles(c.GetProperty("m").GetString()!);
            var m = new double[6];
            CvWarpAffine.RotationMatrix2D(
                c.GetProperty("cx").GetSingle(),
                c.GetProperty("cy").GetSingle(),
                c.GetProperty("angle").GetDouble(),
                c.GetProperty("scale").GetDouble(),
                m);
            for (int i = 0; i < 6; i++)
                Assert.True(m[i] == expected[i],
                    $"m[{i}] = {m[i]:R}, expected {expected[i]:R} " +
                    $"(angle {c.GetProperty("angle").GetDouble():R})");
            cases++;
        }
        Assert.True(cases > 400, $"only {cases} rotation cases");
    }

    [Fact]
    public void Resize_matches_cv2()
    {
        var root = Fixtures.Load("cv_resize.json");
        int u8Cases = 0, f32Cases = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            int sw = c.GetProperty("sw").GetInt32(), sh = c.GetProperty("sh").GetInt32();
            int dw = c.GetProperty("dw").GetInt32(), dh = c.GetProperty("dh").GetInt32();
            var interp = (Interpolation)c.GetProperty("interpolation").GetInt32();
            string tag = $"{sw}x{sh}->{dw}x{dh} {interp} {c.GetProperty("dtype").GetString()}";

            if (c.GetProperty("dtype").GetString() == "u8")
            {
                var src = B64.Bytes(c.GetProperty("src").GetString()!);
                var expected = B64.Bytes(c.GetProperty("dst").GetString()!);
                var dst = new byte[dw * dh];
                CvResize.Resize8u(new GrayImage(sw, sh, src), dst, dw, dh, interp);
                AssertSameBytes(expected, dst, tag);
                u8Cases++;
            }
            else
            {
                var src = B64.Floats(c.GetProperty("src").GetString()!);
                var expected = B64.Floats(c.GetProperty("dst").GetString()!);
                var dst = new float[dw * dh];
                CvResize.Resize32f(src, sw, sh, dst, dw, dh, interp);
                // OpenCV's float32 vertical pass contracts to an FMA chain in its vectorized
                // columns, so this path is only close, not bit-identical (see CvResize).
                _output.WriteLine($"{tag}: max diff {AssertCloseFloats(expected, dst, tag, 1e-6f):R} from cv2");
                f32Cases++;
            }
        }
        Assert.Equal(10, u8Cases);
        Assert.Equal(2, f32Cases);
    }

    [Fact]
    public void WarpAffine_matches_cv2()
    {
        var root = Fixtures.Load("cv_warp.json");
        int u8Cases = 0, f32Cases = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            int sw = c.GetProperty("sw").GetInt32(), sh = c.GetProperty("sh").GetInt32();
            int dw = c.GetProperty("dw").GetInt32(), dh = c.GetProperty("dh").GetInt32();
            var border = (BorderMode)c.GetProperty("border").GetInt32();
            double borderValue = c.GetProperty("borderValue").GetDouble();
            var m = B64.Doubles(c.GetProperty("m").GetString()!);

            if (c.GetProperty("dtype").GetString() == "u8")
            {
                var src = B64.Bytes(c.GetProperty("src").GetString()!);
                var expected = B64.Bytes(c.GetProperty("dst").GetString()!);
                var dst = new byte[dw * dh];
                CvWarpAffine.WarpAffine8u(new GrayImage(sw, sh, src), dst, dw, dh, m, border, borderValue);
                AssertSameBytes(expected, dst, $"u8 {sw}x{sh} {border}");
                u8Cases++;
            }
            else
            {
                var src = B64.Floats(c.GetProperty("src").GetString()!);
                var expected = B64.Floats(c.GetProperty("dst").GetString()!);
                var dst = new float[dw * dh];
                CvWarpAffine.WarpAffine32f(src, sw, sh, dst, dw, dw, dh, m, border, borderValue);
                AssertSameFloats(expected, dst, $"f32 {sw}x{sh} {border}");
                f32Cases++;
            }
        }
        Assert.Equal(10, u8Cases);
        Assert.Equal(4, f32Cases);
    }

    [Fact]
    public void Connected_components_matches_cv2()
    {
        var root = Fixtures.Load("cv_ccl.json");
        int cases = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            int w = c.GetProperty("w").GetInt32(), h = c.GetProperty("h").GetInt32();
            var mask = B64.Bytes(c.GetProperty("mask").GetString()!);
            int expectedCount = c.GetProperty("count").GetInt32();
            var expectedLabels = B64.Ints(c.GetProperty("labels").GetString()!);
            var expectedStats = B64.Ints(c.GetProperty("stats").GetString()!);
            string tag = c.GetProperty("tag").GetString()!;

            var labels = new int[w * h];
            var image = new GrayImage(w, h, mask);
            int count = CvConnectedComponents.LabelComponents4(image, labels);
            Assert.True(count == expectedCount, $"{tag}: count {count} != {expectedCount}");

            for (int i = 0; i < labels.Length; i++)
                Assert.True(labels[i] == expectedLabels[i],
                    $"{tag}: label at ({i % w},{i / w}) = {labels[i]}, expected {expectedLabels[i]}");

            var stats = new int[count * CvConnectedComponents.StatsPerLabel];
            CvConnectedComponents.ComputeStats(labels, w, h, count, stats);
            // Row 0 is the background. OpenCV leaves scratch values there and nothing reads
            // them (the decoder loops over labels 1..count-1), so only real labels are compared.
            for (int i = CvConnectedComponents.StatsPerLabel; i < stats.Length; i++)
                Assert.True(stats[i] == expectedStats[i],
                    $"{tag}: stat[{i}] ({i % 5}) of label {i / 5} = {stats[i]}, expected {expectedStats[i]}");
            cases++;
        }
        Assert.Equal(14, cases);
    }

    private static void AssertSameBytes(byte[] expected, byte[] actual, string tag)
    {
        Assert.True(expected.Length == actual.Length, $"{tag}: length");
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == actual[i],
                $"{tag}: byte {i} = {actual[i]}, expected {expected[i]}");
    }

    private static void AssertSameFloats(float[] expected, float[] actual, string tag)
    {
        Assert.True(expected.Length == actual.Length, $"{tag}: length");
        for (int i = 0; i < expected.Length; i++)
            Assert.True(BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]),
                $"{tag}: float {i} = {actual[i]:R}, expected {expected[i]:R}");
    }

    /// <summary>
    /// Compares within an absolute tolerance (the port's stated parity rule for floats) and
    /// returns the worst difference.
    /// </summary>
    private static float AssertCloseFloats(float[] expected, float[] actual, string tag, float tolerance)
    {
        Assert.True(expected.Length == actual.Length, $"{tag}: length");
        float worst = 0;
        int worstIndex = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            float d = Math.Abs(expected[i] - actual[i]);
            if (d > worst) { worst = d; worstIndex = i; }
        }
        Assert.True(worst <= tolerance,
            $"{tag}: float {worstIndex} = {actual[worstIndex]:R}, expected {expected[worstIndex]:R} " +
            $"(diff {worst:R} > {tolerance:R})");
        return worst;
    }
}
