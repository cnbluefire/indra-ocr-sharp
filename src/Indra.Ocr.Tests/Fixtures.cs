using System.Text.Json;

namespace Indra.Ocr.Tests;

/// <summary>
/// Loads JSON fixtures produced by <c>tools/scripts/test/gen_*.py</c> (which run the
/// Python reference implementation). Fixtures are small text files committed under
/// <c>fixtures/</c> and copied to the test output directory.
/// </summary>
public static class Fixtures
{
    private static readonly Lazy<string> Root = new(() =>
        Path.Combine(AppContext.BaseDirectory, "fixtures"));

    public static JsonElement Load(string name)
    {
        var path = Path.Combine(Root.Value, name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Missing fixture '{name}'. Regenerate with tools/scripts/test/gen_*.py (expected at {path}).");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>Reads a JSON array of numbers as float[].</summary>
    public static float[] FloatArray(JsonElement e)
    {
        var a = new float[e.GetArrayLength()];
        int i = 0;
        foreach (var v in e.EnumerateArray()) a[i++] = v.GetSingle();
        return a;
    }

    /// <summary>Reads a JSON array of numbers as double[].</summary>
    public static double[] DoubleArray(JsonElement e)
    {
        var a = new double[e.GetArrayLength()];
        int i = 0;
        foreach (var v in e.EnumerateArray()) a[i++] = v.GetDouble();
        return a;
    }

    /// <summary>Reads a JSON array of integers as int[].</summary>
    public static int[] IntArray(JsonElement e)
    {
        var a = new int[e.GetArrayLength()];
        int i = 0;
        foreach (var v in e.EnumerateArray()) a[i++] = v.GetInt32();
        return a;
    }

    /// <summary>Reads a JSON array of byte arrays (values 0..255) as byte[][] (rows).</summary>
    public static byte[][] ByteRows(JsonElement e)
    {
        var rows = new byte[e.GetArrayLength()][];
        int i = 0;
        foreach (var row in e.EnumerateArray())
        {
            var a = new byte[row.GetArrayLength()];
            int j = 0;
            foreach (var v in row.EnumerateArray()) a[j++] = (byte)v.GetInt32();
            rows[i++] = a;
        }
        return rows;
    }

    /// <summary>Flattens a 2D JSON array to a 1D float[] in row-major order.</summary>
    public static float[] FlattenFloats2D(JsonElement e)
    {
        var list = new List<float>();
        foreach (var row in e.EnumerateArray())
            foreach (var v in row.EnumerateArray())
                list.Add(v.GetSingle());
        return list.ToArray();
    }
}
