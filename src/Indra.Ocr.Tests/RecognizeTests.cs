using System.Text.Json;
using Indra.Ocr.Imaging;
using Indra.Ocr.Onnx;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for <c>pipeline.OrtOCR.recognize</c> / <c>hanzi_lines</c>
/// (<see cref="Recognizer"/>): line cropping, the hanzi routing, width batching, the CTC collapse
/// and the page x of every kept timestep — see <c>docs/100-recognize.md</c>.
/// </summary>
/// <remarks>
/// The fixture's real cases run <c>rec.onnx</c> / <c>rec_cjk.onnx</c> / <c>script.onnx</c> over
/// the boxes the real detector produced (recorded in the fixture, so no <c>det.onnx</c> run is
/// needed); a deterministic stand-in recognizer and script classifier over a synthetic vocabulary
/// keep the CTC/route/x-mapping logic under test without the model files.
/// </remarks>
public class RecognizeTests
{
    private static readonly JsonElement Fx = Fixtures.Load("recognize.json");

    /// <summary>
    /// The fixture's deterministic recognizer. Mirrors <c>gen_recognize_fixtures.py</c>'s
    /// <c>StandinRec</c> exactly: <c>T = max(1, width / 4)</c> timesteps, a blank every third one,
    /// the two others of a group sharing an id (so the CTC collapse has repeats to drop), and
    /// dyadic confidences (so the kept mean is order-independent).
    /// </summary>
    private sealed class StandInRec : IRecModel
    {
        private readonly int _vocab;
        private readonly float _hi;
        private readonly float _lo;
        private readonly string _mode;
        private readonly int _idBase;

        public StandInRec(int vocab, float hi, float lo, string mode, int idBase)
        {
            _vocab = vocab;
            _hi = hi;
            _lo = lo;
            _mode = mode;
            _idBase = idBase;
        }

        public RecOutput Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height,
                             int width)
        {
            int t = Math.Max(1, width / 4);
            var ids = new long[batch * t];
            var conf = new float[batch * t];
            var lengths = new long[batch];
            for (int b = 0; b < batch; b++)
            {
                long line = (widths[b] + 3) / 4;
                lengths[b] = _mode == "overlong" ? t + 5 : Math.Min(t, line);
                if (_mode == "blank") continue;
                int upto = _mode == "overlong" ? t : (int)lengths[b];
                for (int i = 0; i < upto; i++)
                {
                    if (i % 3 != 2) ids[b * t + i] = 1 + (_idBase + b * 5 + i / 3) % _vocab;
                    conf[b * t + i] = i % 2 == 0 ? _hi : _lo;
                }
            }
            return new RecOutput(ids, conf, lengths, t);
        }
    }

    /// <summary>The fixture's stand-in script classifier: 1.0, 0.5 or 0.25 by batch position.</summary>
    private sealed class StandInScript : IScriptModel
    {
        private static readonly float[] Pattern = { 1.0f, 0.5f, 0.25f };

        public float[] Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height,
                           int width)
        {
            var p = new float[batch];
            for (int b = 0; b < batch; b++) p[b] = Pattern[b % Pattern.Length];
            return p;
        }
    }

    /// <summary>The shipped model set, opened once for the real cases (null when absent).</summary>
    private sealed class Models : IDisposable
    {
        private Models(OcrModels models) => Set = models;

        public OcrModels Set { get; }

        public static Models? TryOpen()
        {
            if (TestModels.Directory is null || !OcrModels.IsComplete(TestModels.Directory) ||
                !File.Exists(Path.Combine(TestModels.Directory, "script.onnx")) ||
                !File.Exists(Path.Combine(TestModels.Directory, "rec_cjk.onnx")))
                return null;     // resources/models is externally downloaded; the fixture is committed
            return new Models(OcrModels.Load(TestModels.Directory, threads: 1));
        }

        public Recognizer Recognizer() => new(
            new OnnxRecModel(Set.Rec), Set.Vocab,
            mask: Set.Mask is null ? null : new OnnxMaskModel(Set.Mask),
            script: new OnnxScriptModel(Set.Script!), recCjk: new OnnxRecModel(Set.RecCjk!),
            vocabCjk: Set.VocabCjk!, height: Set.RecHeight);

        public void Dispose() => Set.Dispose();
    }

    private static IEnumerable<JsonElement> AllCases()
    {
        foreach (var c in Fx.GetProperty("cases").EnumerateArray()) yield return c;
        foreach (var c in Fx.GetProperty("standin").EnumerateArray()) yield return c;
    }

    private static (byte[] Gray, int Width, int Height) Page(string name)
    {
        var p = Fx.GetProperty("pages").GetProperty(name);
        return (B64.ZBytes(p.GetProperty("gray").GetString()!),
                p.GetProperty("w").GetInt32(), p.GetProperty("h").GetInt32());
    }

    private static float[] Boxes(JsonElement c) => B64.ZFloats(c.GetProperty("boxes").GetString()!);

    private static Vocab VocabOf(JsonElement chars)
    {
        var list = new List<string>();
        foreach (var e in chars.EnumerateArray()) list.Add(e.GetString()!);
        return new Vocab(list);
    }

    /// <summary>The recognizer a stand-in case was generated with (its synthetic vocabularies).</summary>
    private static Recognizer StandInRecognizer(JsonElement c)
    {
        var vocab = c.GetProperty("vocab");
        var p = c.GetProperty("params");
        float hi = p.GetProperty("hi").GetSingle();
        float lo = p.GetProperty("lo").GetSingle();
        string mode = p.GetProperty("mode").GetString()!;
        int idBase = p.GetProperty("idbase").GetInt32();

        bool cjk = vocab.ValueKind == JsonValueKind.Object;
        var latinVocab = VocabOf(cjk ? vocab.GetProperty("latin") : vocab);
        var rec = new StandInRec(latinVocab.ClassCount - 1, hi, lo, mode, idBase);
        if (!cjk) return new Recognizer(rec, latinVocab, height: 32);

        var cjkVocab = VocabOf(vocab.GetProperty("cjk"));
        return new Recognizer(rec, latinVocab, script: new StandInScript(),
                              recCjk: new StandInRec(cjkVocab.ClassCount - 1, hi, lo, mode,
                                                     p.GetProperty("cjk_idbase").GetInt32()),
                              vocabCjk: cjkVocab, height: 32);
    }

    private static void AssertLines(JsonElement c, IReadOnlyList<RecognizedLine> actual, bool exactConf)
    {
        string name = c.GetProperty("name").GetString()!;
        var expected = c.GetProperty("lines").EnumerateArray().ToList();
        Assert.True(expected.Count == actual.Count,
                    $"{name}: {actual.Count} lines, expected {expected.Count}");

        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            string tag = $"{name}[{i}]";
            Assert.True(e.GetProperty("text").GetString() == actual[i].Text,
                        $"{tag}: text '{actual[i].Text}', expected '{e.GetProperty("text").GetString()}'");

            double conf = e.GetProperty("conf").GetDouble();
            if (exactConf)
                Assert.True(conf == actual[i].Confidence,
                            $"{tag}: conf {actual[i].Confidence:R}, expected {conf:R}");
            else
                Assert.True(Math.Abs(conf - actual[i].Confidence) <= 1e-6 * Math.Max(Math.Abs(conf), 1e-30),
                            $"{tag}: conf {actual[i].Confidence:R}, expected {conf:R}");

            var tokens = e.GetProperty("tokens").EnumerateArray().ToList();
            Assert.True(tokens.Count == actual[i].Tokens.Count,
                        $"{tag}: {actual[i].Tokens.Count} tokens, expected {tokens.Count}");
            for (int q = 0; q < tokens.Count; q++)
            {
                var t = tokens[q];
                var a = actual[i].Tokens[q];
                string ctag = $"{tag}.token[{q}]";
                Assert.True(t.GetProperty("x").GetDouble() == a.X,
                            $"{ctag}: x {a.X:R}, expected {t.GetProperty("x").GetDouble():R}");
                var ch = t.GetProperty("char");
                if (ch.ValueKind == JsonValueKind.String)
                {
                    Assert.True(a.IsCharacter && a.Character == ch.GetString(),
                                $"{ctag}: token '{a.Character ?? a.Id.ToString()}', " +
                                $"expected '{ch.GetString()}'");
                }
                else
                {
                    Assert.True(!a.IsCharacter && t.GetProperty("id").GetInt64() == a.Id,
                                $"{ctag}: id {a.Id}, expected {t.GetProperty("id").GetInt64()}");
                }
            }
        }
    }

    [Fact]
    public void Recognize_matches_the_reference()
    {
        using var models = Models.TryOpen();
        int cases = 0, lines = 0, tokens = 0, gated = 0, hanzi = 0;
        foreach (var c in AllCases())
        {
            bool real = c.GetProperty("kind").GetString() == "real";
            if (real && models is null) continue;    // the real cases need the shipped models

            string name = c.GetProperty("name").GetString()!;
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);
            var boxes = Boxes(c);
            var recognizer = real ? models!.Recognizer() : StandInRecognizer(c);
            var gray = new GrayImage(w, h, page);

            var result = recognizer.Run(gray, boxes);
            AssertLines(c, result, exactConf: !real);

            if (recognizer.HasCjk)
            {
                var expectedHanzi = c.GetProperty("hanzi").EnumerateArray().ToList();
                var actualHanzi = recognizer.HanziLines(gray, boxes);
                Assert.Equal(expectedHanzi.Count, actualHanzi.Length);
                for (int i = 0; i < actualHanzi.Length; i++)
                    Assert.True(expectedHanzi[i].GetBoolean() == actualHanzi[i],
                                $"{name}: hanzi[{i}] {actualHanzi[i]}");
                hanzi += actualHanzi.Count(x => x);
            }

            foreach (var line in result)
            {
                tokens += line.Tokens.Count;
                if (line.Text.Length == 0 && line.Confidence > 0) gated++;
            }
            lines += result.Count;
            cases++;
        }

        Assert.True(cases >= (models is null ? 8 : 16), $"only {cases} cases");
        Assert.True(lines >= (models is null ? 20 : 60), $"only {lines} lines");
        Assert.True(tokens >= (models is null ? 250 : 800), $"only {tokens} tokens");
        Assert.True(gated >= (models is null ? 3 : 5), $"only {gated} gated lines");
        if (models is not null) Assert.True(hanzi >= 5, $"only {hanzi} hanzi lines");
    }

    [Fact]
    public void Recognize_handles_no_lines()
    {
        using var models = Models.TryOpen();
        foreach (var c in AllCases())
        {
            bool real = c.GetProperty("kind").GetString() == "real";
            if (real && models is null) continue;
            if (c.GetProperty("n").GetInt32() != 0) continue;
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);
            var recognizer = real ? models!.Recognizer() : StandInRecognizer(c);
            var result = recognizer.Run(new GrayImage(w, h, page), Boxes(c));
            Assert.Empty(result);
        }
    }

    [Fact]
    public void Recognizing_without_the_cjk_models_uses_one_group()
    {
        // The reference's `self.script is not None` gate: without the classifier every line goes
        // to the Latin recognizer, whatever the crop holds.
        var c = Fx.GetProperty("standin").EnumerateArray()
                    .First(x => x.GetProperty("name").GetString() == "standin_cjk");
        var (page, w, h) = Page(c.GetProperty("page").GetString()!);
        var boxes = Boxes(c);
        var gray = new GrayImage(w, h, page);

        var withCjk = StandInRecognizer(c);
        Assert.True(withCjk.HasCjk);
        var mixed = withCjk.Run(gray, boxes);
        Assert.Contains(mixed, l => l.Tokens.Any(t => t.IsCharacter));

        var latinVocab = VocabOf(c.GetProperty("vocab").GetProperty("latin"));
        var latinOnly = new Recognizer(
            new StandInRec(latinVocab.ClassCount - 1, 0.75f, 0.25f, "normal", 0), latinVocab,
            height: 32);
        Assert.False(latinOnly.HasCjk);
        var all = latinOnly.Run(gray, boxes);
        Assert.DoesNotContain(all, l => l.Tokens.Any(t => t.IsCharacter));
    }
}
