using System.Text.Json;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Parity tests for the recognizer's vocabulary and the Unicode helpers it leans on.
/// </summary>
/// <remarks>
/// The reference is <c>indra_ocr/vocab.py</c> running on Python 3.14 (<c>unicodedata</c>
/// 16.0.0). The C# port rebuilds the same lists from .NET's Unicode tables, so
/// <see cref="Builders_match_the_reference"/> is the interesting one: it fails loudly if the
/// two Unicode versions ever disagree (see <c>docs/050-vocab.md</c>).
/// </remarks>
public class VocabTests
{
    [Fact]
    public void Builders_match_the_reference()
    {
        var root = Fixtures.Load("vocab.json");
        Assert.Equal(root.GetProperty("counts").GetProperty("latin").GetInt32(),
            Vocab.BuildLatinChars().Length);
        Assert.Equal(StrArray(root.GetProperty("latin")), Vocab.BuildLatinChars());
        Assert.Equal(StrArray(root.GetProperty("multi")), Vocab.BuildMultiTokens());

        var commands = StrArray(root.GetProperty("latex_commands"));
        var multi = Vocab.BuildMultiTokens();
        foreach (var c in commands)
            Assert.Contains("\\" + c, multi);
    }

    [Fact]
    public void FromJson_matches_the_built_vocabulary()
    {
        var root = Fixtures.Load("vocab.json");
        var vocab = Vocab.FromJson(root.GetProperty("meta_vocab_json").GetString()!);
        string[] expected = StrArray(root.GetProperty("multi"));
        Assert.Equal(expected, vocab.Characters);
        Assert.True(vocab.HasCommands);
        Assert.Equal(expected.Length + 1, vocab.ClassCount);
        // meta.json holds the exact to_json() text of the built vocabulary.
        Assert.Equal(root.GetProperty("meta_vocab_json").GetString(), Vocab.Multi().ToJson());
        Assert.Equal(vocab.ToJson(), root.GetProperty("meta_vocab_json").GetString());
    }

    [Fact]
    public void Cjk_vocabulary_has_no_commands()
    {
        var root = Fixtures.Load("vocab.json");
        var cjk = Vocab.FromJson(root.GetProperty("meta_cjk_json").GetString()!);
        Assert.Equal(root.GetProperty("counts").GetProperty("meta_cjk").GetInt32(), cjk.Characters.Length);
        Assert.False(cjk.HasCommands);
        Assert.Equal(cjk.Characters.Length + 1, cjk.ClassCount);

        foreach (var c in root.GetProperty("cjk").EnumerateArray())
        {
            string text = c.GetProperty("text").GetString()!;
            Assert.Equal(StrArray(c.GetProperty("tokens")), cjk.Tokenize(text));
            var ids = c.GetProperty("ids");
            if (ids.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(cjk.Encode(text));
            }
            else
            {
                Assert.Equal(ids.EnumerateArray().Select(x => x.GetInt32()).ToArray(), cjk.Encode(text));
                Assert.Equal(text, cjk.Decode(ids.EnumerateArray().Select(x => x.GetInt32()).ToArray()));
            }
        }
    }

    [Fact]
    public void Canonical_matches_the_reference()
    {
        var root = Fixtures.Load("vocab.json");
        foreach (var c in root.GetProperty("canonical").EnumerateArray())
        {
            string input = c.GetProperty("in").GetString()!;
            string expected = c.GetProperty("out").GetString()!;
            Assert.Equal(expected, UnicodeText.Canonical(input));
        }
    }

    [Fact]
    public void Tokenize_matches_the_reference()
    {
        var root = Fixtures.Load("vocab.json");
        var vocab = Vocab.Multi();
        foreach (var c in root.GetProperty("tokenize").EnumerateArray())
        {
            string text = c.GetProperty("text").GetString()!;
            Assert.Equal(StrArray(c.GetProperty("tokens")), vocab.Tokenize(text));
        }
    }

    [Fact]
    public void Decode_matches_the_reference()
    {
        var root = Fixtures.Load("vocab.json");
        var vocab = Vocab.Multi();
        foreach (var c in root.GetProperty("decode").EnumerateArray())
        {
            int[] ids = c.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(c.GetProperty("text").GetString(), vocab.Decode(ids));
        }
    }

    [Fact]
    public void Encode_matches_the_reference()
    {
        var root = Fixtures.Load("vocab.json");
        var vocab = Vocab.Multi();
        foreach (var c in root.GetProperty("encode").EnumerateArray())
        {
            var v = c.TryGetProperty("small", out _) ? new Vocab(["a", "b", "c"]) : vocab;
            string text = c.GetProperty("text").GetString()!;
            var ids = c.GetProperty("ids");
            if (ids.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(v.Encode(text));
            }
            else
            {
                Assert.Equal(ids.EnumerateArray().Select(x => x.GetInt32()).ToArray(), v.Encode(text));
            }
        }
    }

    [Fact]
    public void Oov_matches_the_reference()
    {
        var root = Fixtures.Load("vocab.json");
        var vocab = Vocab.Multi();
        foreach (var c in root.GetProperty("oov").EnumerateArray())
        {
            string text = c.GetProperty("text").GetString()!;
            Assert.Equal(StrArray(c.GetProperty("tokens")).OrderBy(x => x, StringComparer.Ordinal),
                vocab.Oov(text).OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Indic_checks_match_the_reference()
    {
        var root = Fixtures.Load("vocab.json");
        foreach (var c in root.GetProperty("indic").EnumerateArray())
        {
            string text = c.GetProperty("text").GetString()!;
            Assert.Equal(c.GetProperty("valid").GetBoolean(), UnicodeText.IndicValid(text));
        }
        foreach (var c in root.GetProperty("indic_page").EnumerateArray())
        {
            string[] texts = StrArray(c.GetProperty("texts"));
            Assert.Equal(c.GetProperty("ok").GetBoolean(), UnicodeText.IndicPageOk(texts));
        }
    }

    [Fact]
    public void ToJson_escapes_like_python()
    {
        var root = Fixtures.Load("vocab.json");
        foreach (var c in root.GetProperty("to_json").EnumerateArray())
        {
            var json = c.GetProperty("json").GetString()!;
            var vocab = Vocab.FromJson(json);
            Assert.Equal(json, vocab.ToJson());
        }
    }

    private static string[] StrArray(JsonElement e) =>
        e.EnumerateArray().Select(x => x.GetString()!).ToArray();
}
