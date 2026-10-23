using System.IO.Compression;
using Indra.Ocr.Rec;

namespace Indra.Ocr.Tests;

public class LanguageModelTests
{
    [Fact]
    public void Wide_token_keys_and_backoff_values_are_read_from_npz()
    {
        string path = Path.Combine(Path.GetTempPath(), $"indra-tiny-lm-{Guid.NewGuid():N}.npz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "order", "<i8", BitConverter.GetBytes(2L), "()");
            Add(zip, "vocab_size", "<i8", BitConverter.GetBytes(3000L), "()");
            Add(zip, "bits", "<i8", BitConverter.GetBytes(12L), "()");
            Add(zip, "k0", "<u8", Ulongs(2501), "(1,)");
            Add(zip, "v0", "<f4", Floats(-1), "(1,)");
            Add(zip, "hk0", "<u8", Ulongs(0), "(1,)");
            Add(zip, "hb0", "<f4", Floats(0), "(1,)");
            Add(zip, "k1", "<u8", Ulongs(ulong.MaxValue), "(1,)");
            Add(zip, "v1", "<f4", Floats(-1), "(1,)");
            Add(zip, "hk1", "<u8", Ulongs(ulong.MaxValue), "(1,)");
            Add(zip, "hb1", "<f4", Floats(0), "(1,)");
        }
        var lm = new NgramLanguageModel(path);
        Assert.Equal(12, lm.Bits);
        Assert.Equal(-1, lm.LogProbability(Array.Empty<int>(), 2500), 6);
    }

    private static byte[] Ulongs(params ulong[] values) =>
        values.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] Floats(params float[] values) =>
        values.SelectMany(BitConverter.GetBytes).ToArray();

    private static void Add(ZipArchive zip, string name, string dtype, byte[] body, string shape)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name + ".npy");
        using Stream stream = entry.Open();
        stream.Write(new byte[] { 0x93, 0x4e, 0x55, 0x4d, 0x50, 0x59, 1, 0 });
        string raw = $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': {shape}, }}";
        int length = ((raw.Length + 1 + 2 + 8 + 15) / 16) * 16 - 10;
        string header = raw.PadRight(length - 1) + "\n";
        stream.Write(BitConverter.GetBytes((ushort)header.Length));
        stream.Write(System.Text.Encoding.ASCII.GetBytes(header));
        stream.Write(body);
    }
}
