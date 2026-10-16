using System.Buffers.Binary;
using System.IO.Compression;

namespace Indra.Ocr.Tests;

/// <summary>
/// Decodes the raw little-endian arrays the CV fixtures store (see
/// <c>tools/scripts/test/gen_cv_fixtures.py</c>). Base64 of the raw bytes keeps the fixture
/// exact, so tests can assert bit-parity rather than approximate equality.
/// <para>
/// The <c>Z*</c> overloads first inflate the bytes (zlib), which the larger fixtures —
/// currently only <c>det_post.json</c> — use to stay a reasonable size.
/// </para>
/// </summary>
public static class B64
{
    public static byte[] Bytes(string s) => Convert.FromBase64String(s);

    public static byte[] ZBytes(string s) => Inflate(s);

    public static float[] ZFloats(string s)
    {
        var raw = Inflate(s);
        var a = new float[raw.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
        return a;
    }

    private static byte[] Inflate(string s)
    {
        using var input = new MemoryStream(Convert.FromBase64String(s));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public static int[] Ints(string s)
    {
        var raw = Convert.FromBase64String(s);
        var a = new int[raw.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
        return a;
    }

    /// <summary>Inflated little-endian int32 array.</summary>
    public static int[] ZInts(string s)
    {
        var raw = Inflate(s);
        var a = new int[raw.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
        return a;
    }

    /// <summary>Inflated little-endian float64 array.</summary>
    public static double[] ZDoubles(string s)
    {
        var raw = Inflate(s);
        var a = new double[raw.Length / 8];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(i * 8));
        return a;
    }

    /// <summary>Reads a 16-character little-endian hex string (``struct.pack('&lt;d', x).hex()``)
    /// as a double, so fixtures can carry exact bit patterns including <c>-0.0</c>.</summary>
    public static double HexDouble(string s) => BinaryPrimitives.ReadDoubleLittleEndian(Convert.FromHexString(s));

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
