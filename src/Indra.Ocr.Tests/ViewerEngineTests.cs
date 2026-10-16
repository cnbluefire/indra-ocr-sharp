using System.Text.Json;
using Indra.Ocr.Py;
using Indra.Ocr.Viewer;

namespace Indra.Ocr.Tests;

/// <summary>
/// Step 210: the viewer's engine — <c>Engine.upload</c> and <c>Engine.page</c> — against
/// <c>fixtures/viewer.json</c>, which the Python reference wrote by driving its own
/// <c>viewer.Engine</c> over the sample PDFs and a synthetic page.
/// </summary>
/// <remarks>
/// The fixture records the page bytes (base64), so the test needs no files; the model folder is
/// external material that is not in git, so the test returns early without it.
/// <para>
/// Two parts of a response are not compared: the JPEG preview (Python encodes with OpenCV's
/// libjpeg, the port with ImageSharp, so the bytes differ — only the prefix is checked) and the
/// timing <em>values</em> (wall clock; the keys and their order are compared). Everything else —
/// the size, the lines, the blocks, the Markdown, the rotation and the CLI-shaped JSON — is
/// compared field by field.
/// </para>
/// <para>
/// The class drives PDFium through the engine, so it shares the serialising collection.
/// </para>
/// </remarks>
[Collection("pdfium")]
public class ViewerEngineTests
{
    private static readonly JsonElement Fx = Fixtures.Load("viewer.json");

    private static readonly JsonSerializerOptions Naming = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>One engine for the whole class: loading the models is the slow part.</summary>
    private static readonly Lazy<Engine?> Shared = new(Create);

    private static Engine? Create()
    {
        string? models = TestModels.Directory;
        if (models is null)
        {
            return null;
        }

        JsonElement d = Fx.GetProperty("defaults");
        var defaults = new ViewerDefaults(
            d.GetProperty("mode").GetString()!,
            d.GetProperty("pdf_order").GetBoolean(),
            d.GetProperty("image_ocr").GetBoolean(),
            d.GetProperty("math").GetBoolean(),
            d.GetProperty("furniture").GetBoolean(),
            d.GetProperty("dpi").GetDouble(),
            d.GetProperty("max_pages").GetInt32());
        return new Engine(PyPath.From(models), threads: 4, tesseract: false, defaults);
    }

    private static byte[] Input(string name)
        => Convert.FromBase64String(
            Fx.GetProperty("inputs").GetProperty(name).GetProperty("b64").GetString()!);

    private static PageOptions ReadOptions(JsonElement opts)
    {
        bool Flag(string key) => opts.TryGetProperty(key, out JsonElement v) && v.GetBoolean();
        return new PageOptions(
            Mode: opts.TryGetProperty("mode", out JsonElement mode) ? mode.GetString() : null,
            Math: Flag("math"),
            Furniture: Flag("furniture"),
            Dpi: opts.TryGetProperty("dpi", out JsonElement dpi) ? dpi.GetDouble() : null,
            ModelOrder: Flag("model_order"),
            ImageOcr: opts.TryGetProperty("image_ocr", out JsonElement imageOcr)
                ? imageOcr.GetBoolean()
                : null,
            Tesseract: Flag("tesseract"));
    }

    [Fact]
    public void Page_matches_the_reference_engine()
    {
        Engine? engine = Shared.Value;
        if (engine is null)
        {
            return;
        }

        foreach (JsonElement c in Fx.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            string source = c.GetProperty("input").GetString()!;
            int index = c.GetProperty("n").GetInt32();

            UploadResponse uploaded = engine.Upload(Input(source));
            Assert.Equal(Fx.GetProperty("uploads").GetProperty(source).GetInt32(), uploaded.Pages);

            PageResponse got = engine.Page(uploaded.Id, index, ReadOptions(c.GetProperty("opts")));
            Assert.StartsWith("data:image/jpeg;base64,", got.Img);
            Assert.Null(got.Tesseract);

            JsonElement actual = JsonSerializer.SerializeToElement(got, Naming);
            var problems = new List<string>();
            Compare(c.GetProperty("page"), actual, name, problems);
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
    }

    [Fact]
    public void Errors_match_the_reference()
    {
        Engine? engine = Shared.Value;
        if (engine is null)
        {
            return;
        }

        foreach (JsonElement e in Fx.GetProperty("errors").EnumerateArray())
        {
            PageOptions opts = ReadOptions(e.GetProperty("opts"));
            JsonElement input = e.GetProperty("input");
            string expected = e.GetProperty("error").GetString()!;
            if (input.ValueKind == JsonValueKind.Null)
            {
                ViewerError unknown = Assert.Throws<ViewerError>(() => engine.Page("nope", 0, opts));
                Assert.Equal(expected, unknown.Message);
                continue;
            }

            UploadResponse uploaded = engine.Upload(Input(input.GetString()!));
            ViewerError thrown =
                Assert.Throws<ViewerError>(() => engine.Page(uploaded.Id, 0, opts));
            Assert.Equal(expected, thrown.Message);
        }
    }

    [Fact]
    public void Upload_errors_match_the_reference()
    {
        Engine? engine = Shared.Value;
        if (engine is null)
        {
            return;
        }

        foreach (JsonElement e in Fx.GetProperty("upload_errors").EnumerateArray())
        {
            byte[] data = Convert.FromBase64String(e.GetProperty("b64").GetString()!);
            ViewerError thrown = Assert.Throws<ViewerError>(() => engine.Upload(data));
            Assert.Equal(e.GetProperty("error").GetString(), thrown.Message);
        }
    }

    [Fact]
    public void Keeps_only_the_last_five_uploads()
    {
        Engine? engine = Shared.Value;
        if (engine is null)
        {
            return;
        }

        var ids = new List<string>();
        foreach (string name in new[]
                 {
                     "synthetic", "text", "rotated", "image", "synthetic-90", "synthetic-180",
                 })
        {
            ids.Add(engine.Upload(Input(name)).Id);
        }

        // The oldest fell off; the newest is still there.
        Assert.Throws<ViewerError>(() => engine.Page(ids[0], 0, new PageOptions()));
        Assert.Equal(1, engine.Page(ids[5], 0, new PageOptions()).Page);
    }

    /// <summary>
    /// Compares the fixture's page record with the port's response, both as JSON: every key the
    /// reference recorded must be there, with the same value (numbers within 1e-6).
    /// </summary>
    private static void Compare(JsonElement expected, JsonElement actual, string path,
                                List<string> problems)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in expected.EnumerateObject())
                {
                    if (!actual.TryGetProperty(property.Name, out JsonElement value))
                    {
                        problems.Add($"{path}.{property.Name}: missing");
                        continue;
                    }

                    // The timings are recorded as their keys: the values are wall clock.
                    if (property.Name == "timings" && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        string want = string.Join(",", property.Value.EnumerateArray()
                            .Select(v => v.GetString()));
                        string have = string.Join(",", value.EnumerateObject().Select(p => p.Name));
                        if (want != have)
                        {
                            problems.Add($"{path}.timings: expected [{want}], got [{have}]");
                        }

                        continue;
                    }

                    Compare(property.Value, value, $"{path}.{property.Name}", problems);
                }

                break;
            case JsonValueKind.Array:
                if (actual.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"{path}: expected an array, got {actual.ValueKind}");
                    break;
                }

                int wantLength = expected.GetArrayLength();
                int haveLength = actual.GetArrayLength();
                if (wantLength != haveLength)
                {
                    problems.Add($"{path}: expected {wantLength} items, got {haveLength}");
                    break;
                }

                int i = 0;
                foreach (JsonElement item in expected.EnumerateArray())
                {
                    Compare(item, actual[i++], $"{path}[{i - 1}]", problems);
                }

                break;
            case JsonValueKind.String:
                if (actual.ValueKind != JsonValueKind.String ||
                    expected.GetString() != actual.GetString())
                {
                    problems.Add($"{path}: expected \"{expected.GetString()}\", " +
                                 $"got \"{actual.GetString()}\"");
                }

                break;
            case JsonValueKind.Number:
                if (actual.ValueKind != JsonValueKind.Number ||
                    Math.Abs(expected.GetDouble() - actual.GetDouble()) > 1e-6)
                {
                    problems.Add($"{path}: expected {expected.GetDouble():R}, " +
                                 $"got {actual.ToString()}");
                }

                break;
            default:
                if (expected.ValueKind != actual.ValueKind)
                {
                    problems.Add($"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
                }

                break;
        }
    }
}
