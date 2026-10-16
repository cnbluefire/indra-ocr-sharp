using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Indra.Ocr.Cli.Worker;

/// <summary>The worker processes stopped: the pool's stand-in for <c>BrokenProcessPool</c>.</summary>
internal sealed class BrokenPoolException : Exception
{
    public BrokenPoolException(Exception cause) : base(cause.Message, cause)
    {
    }
}

/// <summary>
/// The parent half of the pool: one worker process per core, one page per task.
/// </summary>
/// <remarks>
/// The reference uses <c>ProcessPoolExecutor.map</c>: every unit is submitted, workers take
/// whichever unit is next, and the results come back in submission order. The port reproduces
/// that with a cursor and one task per worker (<see cref="Pump"/>): each worker task takes the
/// next unit, writes it, and reads its own result, publishing it by index. Because every worker
/// task drains its own stdout, no pipe can fill up behind a computing worker, and the parent —
/// the caller's <c>foreach</c> — only waits on <see cref="Result"/> for the index it needs.
/// </remarks>
internal sealed class WorkerPool : IDisposable
{
    private readonly Process[] _processes;
    private readonly BinaryWriter[] _writers;
    private readonly BinaryReader[] _readers;
    private readonly Task[] _tasks;
    private readonly WorkResult?[] _results;
    private readonly List<WorkUnit> _units;
    private readonly object _lock = new();
    private int _next;
    private Exception? _failure;
    private bool _disposed;

    /// <summary>Start <paramref name="workers"/> processes and hand them the units.</summary>
    public WorkerPool(int workers, LoadOptions options, int threads, List<WorkUnit> units)
    {
        _units = units;
        _results = new WorkResult?[units.Count];
        _processes = new Process[workers];
        _writers = new BinaryWriter[workers];
        _readers = new BinaryReader[workers];
        _tasks = new Task[workers];

        try
        {
            for (int i = 0; i < workers; i++)
            {
                Process process = Process.Start(WorkerStartInfo())!;
                _processes[i] = process;
                _writers[i] = new BinaryWriter(process.StandardInput.BaseStream, Utf8);
                _readers[i] = new BinaryReader(process.StandardOutput.BaseStream, Utf8);
                WorkerProtocol.WriteConfig(_writers[i], options, threads);
            }
        }
        catch
        {
            // A pool that cannot start is the reference's BrokenProcessPool: stop what did
            // start and let the caller read the inputs in this process.
            Dispose();
            throw;
        }

        for (int i = 0; i < workers; i++)
        {
            int worker = i;
            _tasks[i] = Task.Factory.StartNew(() => Pump(worker), TaskCreationOptions.LongRunning);
        }
    }

    /// <summary>The result of one unit, in submission order (the reference's <c>ex.map</c>).</summary>
    /// <exception cref="BrokenPoolException">A worker stopped before answering.</exception>
    public WorkResult Result(int index)
    {
        lock (_lock)
        {
            while (_results[index] is null)
            {
                if (_failure is not null)
                {
                    throw new BrokenPoolException(_failure);
                }

                Monitor.Wait(_lock);
            }

            return _results[index]!;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The reference's shutdown(wait=False, cancel_futures=True): stop the workers, then
        // let their tasks notice (their reads fail) before the streams go away.
        foreach (Process? process in _processes)
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
            {
                // Already gone, or gone in a way we cannot help.
            }
        }

        try
        {
            Task.WaitAll([.. _tasks.Where(task => task is not null)], TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // A task that failed on the way out: nothing left to do about it.
        }

        foreach (BinaryWriter? writer in _writers)
        {
            writer?.Dispose();
        }

        foreach (BinaryReader? reader in _readers)
        {
            reader?.Dispose();
        }

        foreach (Process? process in _processes)
        {
            process?.Dispose();
        }
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Take the next unit, run it in this worker, publish the result by index.</summary>
    private void Pump(int worker)
    {
        try
        {
            while (true)
            {
                int index = Interlocked.Increment(ref _next) - 1;
                if (index >= _units.Count)
                {
                    break;
                }

                WorkerProtocol.WriteUnit(_writers[worker], _units[index]);
                WorkResult result = WorkerProtocol.ReadResult(_readers[worker]);
                lock (_lock)
                {
                    _results[index] = result;
                    Monitor.PulseAll(_lock);
                }
            }
        }
        catch (Exception e)
        {
            lock (_lock)
            {
                _failure ??= e;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// How to start a worker: this same program, with the worker variable set.
    /// </summary>
    private static ProcessStartInfo WorkerStartInfo()
    {
        // Only the command line pools: a host that embeds the library (a test runner, say) has
        // no worker mode, and "the pool cannot start" is the reference's BrokenProcessPool.
        if (Assembly.GetEntryAssembly() != typeof(WorkerPool).Assembly)
        {
            throw new InvalidOperationException("the worker processes are not available here");
        }

        string host = Environment.ProcessPath
                      ?? throw new InvalidOperationException("the process path is not known");
        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Environment.CurrentDirectory,
            CreateNoWindow = true,
        };

        // Run as `dotnet indra-ocr.dll`: the host needs the assembly as its first argument. The
        // entry assembly is asked for by name in the base directory rather than through
        // Assembly.Location, which is empty in a single-file or NativeAOT build (IL3000) — and a
        // `dotnet` host is exactly the case that cannot happen there, because such a build is its
        // own host. (The check above already proved this assembly *is* the entry assembly.)
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string name = typeof(WorkerPool).Assembly.GetName().Name!;
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
        }

        info.Environment[WorkerProtocol.WorkerVariable] = "1";
        return info;
    }
}
