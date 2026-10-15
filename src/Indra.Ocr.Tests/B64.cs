using System.Buffers.Binary;

namespace Indra.Ocr.Tests;

/// <summary>
/// Decodes the raw little-endian arrays the CV fixtures store (see
/// <c>tools/scripts/test/gen_cv_fixtures.py</c>). Base64 of the raw bytes keeps the fixture
/// exact, so tests can assert bit-parity rather than approximate equality.
/// </summary>
public static class B64
{
    public static byte[] Bytes(string s) => Convert.FromBase64String(s);

    public static int[] Ints(string s)
    {
        var raw = Convert.FromBase64String(s);
        var a = new int[raw.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
        return a;
    }

    public static float[] Floats(string s)
    {
        var raw = Convert.FromBase64String(s);
        var a = new float[raw.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
        return a;
    }

    public static double[] Doubles(string s)
    {
        var raw = Convert.FromBase64String(s);
        var a = new double[raw.Length / 8];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(i * 8));
        return a;
    }
}
