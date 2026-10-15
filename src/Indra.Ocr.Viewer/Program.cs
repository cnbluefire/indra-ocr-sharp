// indra-ocr-viewer (C# port) — placeholder host. Minimal API arrives in step 220.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "indra-ocr-viewer: not implemented yet");

app.Run();
