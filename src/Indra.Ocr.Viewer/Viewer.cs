using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Indra.Ocr.Argparse;
using Indra.Ocr.Onnx;
using Indra.Ocr.Py;

namespace Indra.Ocr.Viewer;

/// <summary>
/// The port of <c>indra_ocr.viewer</c>: the local viewer — drop in a PDF or an image and see what
/// Indra OCR reads.
/// </summary>
/// <remarks>
/// The command line is the reference's argparse, so <c>--help</c>, <c>--version</c> and the error
/// texts match; the work is <see cref="Engine"/>; this file is the HTTP layer — the five routes
/// the reference's handler serves, over ASP.NET Core's minimal API. Answers are written with the
/// source-generated <see cref="ViewerJson"/> (step 220).
/// <para>
/// The transport cannot match the reference's <c>BaseHTTPRequestHandler</c> header for header
/// (HTTP/1.0 vs HTTP/1.1, <c>Server:</c>, <c>Date:</c>, keep-alive), so what has to match is the
/// status, the content type and the body.
/// </para>
/// </remarks>
internal static class Viewer
{
    /// <summary>
    /// <c>viewer.main</c>: the server runs until it is stopped (exit 0); a missing model set is one
    /// line and exit 1, and a refused option is the same.
    /// </summary>
    public static int Run(string[] args)
    {
        try
        {
            return Serve(args);
        }
        catch (ArgparseExit exit)
        {
            // argparse writes --help and --version to stdout and an error to stderr, then exits.
            if (exit.Stdout.Length > 0)
            {
                PyConsole.Write(exit.Stdout);
            }

            if (exit.Stderr.Length > 0)
            {
                PyConsole.WriteError(exit.Stderr);
            }

            return exit.Status;
        }
        catch (ModelError e)
        {
            PyConsole.WriteError($"indra-ocr-viewer: {e.Message}\n");
            return 1;
        }
        catch (ViewerError e)
        {
            PyConsole.WriteError($"indra-ocr-viewer: {e.Message}\n");
            return 1;
        }
    }

    /// <summary><c>viewer._main</c>: the parser, the engine, and the server.</summary>
    private static int Serve(string[] args)
    {
        ViewerArgs a = ViewerParser.Read(ViewerParser.Build().ParseArgs(args));
        using var engine = new Engine(a.ModelDir, a.Threads, a.Tesseract, a.Defaults);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // the reference's log_message() is a no-op
        builder.WebHost.UseUrls($"http://127.0.0.1:{a.Port}");
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
        WebApplication app = builder.Build();

        app.MapGet("/", () => Html());
        app.MapGet("/index.html", () => Html());
        // "tesseract" answers whether the Tesseract backend could be loaded; the port refuses
        // --tesseract at startup (Engine's constructor), so a running server never has one.
        app.MapGet("/info", () => Json(
            new InfoResponse(Tesseract: false, Version: ProductInfo.Version, Defaults: engine.Defaults),
            ViewerJson.Default.InfoResponse));

        // POST /upload: the raw bytes of the file. POST /page: {"id", "n", "opts"}. Both answer
        // with the reference's 400 + {"error": …} for anything that goes wrong (it catches
        // Exception there, not just its own errors).
        app.MapPost("/upload", async (HttpRequest request) =>
        {
            try
            {
                return Json(engine.Upload(await Body(request)), ViewerJson.Default.UploadResponse);
            }
            catch (Exception e)
            {
                return Fail(e);
            }
        });

        app.MapPost("/page", async (HttpRequest request) =>
        {
            try
            {
                PageRequest q = JsonSerializer.Deserialize(await Body(request),
                                                           ViewerJson.Default.PageRequest)
                                ?? throw new ViewerError("'id'"); // json.loads("null") is None
                if (q.Id is null)
                {
                    throw new ViewerError("'id'"); // q["id"], i.e. Python's KeyError text
                }

                if (q.N is null)
                {
                    throw new ViewerError("'n'"); // int(q["n"])
                }

                return Json(engine.Page(q.Id, q.N.Value, q.Opts ?? new PageOptions()),
                            ViewerJson.Default.PageResponse);
            }
            catch (Exception e)
            {
                return Fail(e);
            }
        });

        app.MapFallback(() => Send("not found"u8.ToArray(), "text/plain", 404));

        PyConsole.Write($"Indra OCR viewer on http://localhost:{a.Port}  ({a.Threads} threads)\n");
        app.Run();
        return 0;
    }

    /// <summary>The reference's <c>self.rfile.read(n)</c>: the whole request body.</summary>
    private static async Task<byte[]> Body(HttpRequest request)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// The reference's <c>_send</c>: the status, <c>Content-Type</c>, <c>Content-Length</c> and the
    /// body — written by hand so the content type is spelled exactly (<c>application/json</c>, not
    /// <c>application/json; charset=utf-8</c>).
    /// </summary>
    private static IResult Send(byte[] body, string contentType, int status = 200)
        => new SendResult(body, contentType, status);

    /// <summary>An answer serialized by the generated context, spelled <c>application/json</c>.</summary>
    private static IResult Json<T>(T value, JsonTypeInfo<T> info)
        => Send(JsonSerializer.SerializeToUtf8Bytes(value, info), "application/json");

    /// <summary>
    /// The reference's <c>except Exception</c>: the reason on stdout (the server's own console),
    /// then <c>400</c> with <c>{"error": …}</c>.
    /// </summary>
    private static IResult Fail(Exception e)
    {
        PyConsole.Write($"error: {e.Message}\n");
        return Send(JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse(e.Message),
                                                        ViewerJson.Default.ErrorResponse),
                    "application/json", 400);
    }

    /// <summary><c>viewer.PAGE.replace("__LOGO__", LOGO)</c>, UTF-8 encoded.</summary>
    private static IResult Html()
        => Send(Encoding.UTF8.GetBytes(ViewerPage.Render()), "text/html; charset=utf-8");

    private sealed record SendResult(byte[] Body, string ContentType, int Status) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = Status;
            httpContext.Response.ContentType = ContentType;
            httpContext.Response.ContentLength = Body.Length;
            return httpContext.Response.Body.WriteAsync(Body).AsTask();
        }
    }
}
