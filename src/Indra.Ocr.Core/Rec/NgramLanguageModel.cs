using System.IO.Compression;
using System.Text;

namespace Indra.Ocr.Rec;

/// <summary>The compact backoff n-gram table stored by upstream in <c>lm*.npz</c>.</summary>
public sealed class NgramLanguageModel
{
    private readonly ulong[][] _keys;
    private readonly float[][] _values;
    private readonly ulong[][] _historyKeys;
    private readonly float[][] _backoffs;

    public NgramLanguageModel(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Order = checked((int)Npy.Int64(zip, "order"));
        VocabSize = checked((int)Npy.Int64(zip, "vocab_size"));
        Bits = Npy.Has(zip, "bits") ? checked((int)Npy.Int64(zip, "bits")) : 11;
        if (Order * Bits > 61) throw new ArgumentException($"order {Order} x {Bits} bits do not fit a key");
        _keys = new ulong[Order][];
        _values = new float[Order][];
        _historyKeys = new ulong[Order][];
        _backoffs = new float[Order][];
        for (int n = 0; n < Order; n++)
        {
            _keys[n] = Npy.UInt64(zip, $"k{n}");
            _values[n] = Npy.Float32(zip, $"v{n}");
            _historyKeys[n] = Npy.UInt64(zip, $"hk{n}");
            _backoffs[n] = Npy.Float32(zip, $"hb{n}");
        }
        double top = -Math.Log(VocabSize);
        foreach (float[] values in _values) foreach (float v in values) top = Math.Max(top, v);
        foreach (float[] values in _backoffs)
        {
            float max = values.Length == 0 ? 0 : values.Max();
            if (max > 0) top += max;
        }
        MaxLogProbability = top;
    }

    public int Order { get; }
    public int VocabSize { get; }
    public int Bits { get; }
    public double MaxLogProbability { get; }

    public double LogProbability(IReadOnlyList<int> history, int token)
    {
        int top = Math.Min(history.Count, Order - 1);
        int seen = 0;
        for (int n = 1; n <= top; n++)
        {
            ulong h = Pack(history, history.Count - n, n);
            if (Find(_historyKeys[n], h) < 0) break;
            seen = n;
        }
        double accumulated = 0;
        for (int n = seen; n >= 0; n--)
        {
            ulong h = n == 0 ? 0 : Pack(history, history.Count - n, n);
            ulong key = (h << Bits) | (uint)(token + 1);
            int p = Find(_keys[n], key);
            if (p >= 0) return accumulated + _values[n][p];
            int hi = Find(_historyKeys[n], h);
            accumulated += _backoffs[n][hi];
        }
        return accumulated - Math.Log(VocabSize);
    }

    private ulong Pack(IReadOnlyList<int> tokens, int start, int count)
    {
        ulong key = 0;
        for (int i = 0; i < count; i++) key = (key << Bits) | (uint)(tokens[start + i] + 1);
        return key;
    }

    private static int Find(ulong[] values, ulong key)
    {
        int i = Array.BinarySearch(values, key);
        return i >= 0 ? i : -1;
    }

    private static class Npy
    {
        public static bool Has(ZipArchive zip, string name) => zip.GetEntry(name + ".npy") is not null;
        public static long Int64(ZipArchive z, string n) => BitConverter.ToInt64(Bytes(z, n, "<i8"), 0);
        public static ulong[] UInt64(ZipArchive z, string n)
        {
            byte[] b = Bytes(z, n, "<u8"); var a = new ulong[b.Length / 8];
            for (int i = 0; i < a.Length; i++) a[i] = BitConverter.ToUInt64(b, i * 8); return a;
        }
        public static float[] Float32(ZipArchive z, string n)
        {
            byte[] b = Bytes(z, n, "<f4"); var a = new float[b.Length / 4];
            for (int i = 0; i < a.Length; i++) a[i] = BitConverter.ToSingle(b, i * 4); return a;
        }
        private static byte[] Bytes(ZipArchive zip, string name, string dtype)
        {
            using Stream s = zip.GetEntry(name + ".npy")?.Open() ?? throw new InvalidDataException(name);
            byte[] magic = new byte[8]; ReadExact(s, magic);
            byte[] expected = { 0x93, 0x4e, 0x55, 0x4d, 0x50, 0x59 };
            bool valid = true;
            for (int i = 0; i < expected.Length; i++) valid &= magic[i] == expected[i];
            if (!valid)
                throw new InvalidDataException($"{name}: not npy");
            int headerLength;
            if (magic[6] == 1) { byte[] n = new byte[2]; ReadExact(s, n); headerLength = n[0] | n[1] << 8; }
            else { byte[] n = new byte[4]; ReadExact(s, n); headerLength = BitConverter.ToInt32(n, 0); }
            var header = new byte[headerLength]; ReadExact(s, header);
            string text = Encoding.ASCII.GetString(header);
            if (!text.Contains("'descr': '" + dtype + "'")) throw new InvalidDataException($"{name}: {text}");
            using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }
        private static void ReadExact(Stream s, byte[] b)
        {
            int p = 0; while (p < b.Length) { int n = s.Read(b, p, b.Length - p); if (n == 0) throw new EndOfStreamException(); p += n; }
        }
    }
}

public readonly record struct LanguageModelSettings(NgramLanguageModel Model, double Alpha,
                                                    double Beta, double Gate);
