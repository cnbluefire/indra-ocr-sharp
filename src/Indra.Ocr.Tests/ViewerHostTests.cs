using System.Net;
using System.Net.Sockets;
using Indra.Ocr.Viewer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Indra.Ocr.Tests;

/// <summary>
/// The viewer's host: the lifetime that turns Ctrl+C into a shutdown, and the shutdown itself.
/// </summary>
/// <remarks>
/// <c>WebApplication.CreateSlimBuilder</c> builds the *empty* host
/// (<c>Host.CreateEmptyApplicationBuilder</c>), which registers no <c>IHostLifetime</c> at all: a
/// Ctrl+C would kill the process instead of stopping it. The viewer registers the console
/// lifetime for it (step 300), and this is what pins that down. The signal itself cannot be
/// raised from a test, so what is checked is what the signal *does*: the host has a lifetime, and
/// stopping the application ends the run and gives the port back.
/// </remarks>
public class ViewerHostTests
{
    [Fact]
    public async Task Stopping_the_application_ends_the_server()
    {
        if (TestModels.Directory is not { } models)
        {
            return; // the model folder is externally downloaded material
        }

        int port = FreePort();
        var args = new ViewerArgs(
            ModelDir: models,
            Port: port,
            Threads: 1,
            Tesseract: false,
            TesseractLang: "eng",
            Defaults: new ViewerDefaults(Mode: "auto", ImageOcr: true, Math: false,
                                         Furniture: false, Dpi: 200.0, MaxPages: 20));
        using var engine = new Engine(args.ModelDir, args.Threads, args.Tesseract, args.Defaults);
        WebApplication app = Indra.Ocr.Viewer.Viewer.BuildHost(args, engine);

        // The console lifetime is what Ctrl+C is wired to. Anything else — or none at all —
        // means the signal is the process's own death and StopApplication is never called.
        IHostLifetime? lifetime = app.Services.GetService<IHostLifetime>();
        Assert.NotNull(lifetime);
        Assert.Equal("ConsoleLifetime", lifetime.GetType().Name);

        await app.StartAsync();
        using (var http = new HttpClient())
        {
            using HttpResponseMessage info = await http.GetAsync($"http://127.0.0.1:{port}/info");
            Assert.True(info.IsSuccessStatusCode, $"/info answered {(int)info.StatusCode}");
        }

        // What the console lifetime does with the signal, and what app.Run() does with the
        // result: the application stops, the run ends, and the server is down.
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await app.WaitForShutdownAsync();
        await app.StopAsync();

        using var probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync("127.0.0.1", port));
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
