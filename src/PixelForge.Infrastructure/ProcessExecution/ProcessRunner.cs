using System.Diagnostics;
using System.Text;
using PixelForge.Domain.Interfaces;

namespace PixelForge.Infrastructure.ProcessExecution;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
        await RunStreamingAsync(request, null, null, cancellationToken);

    public async Task<ProcessResult> RunStreamingAsync(ProcessRequest request,
        Action<string>? onStdOut, Action<string>? onStdErr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.FileName))
        {
            throw new FileNotFoundException($"The executable was not found: {request.FileName}", request.FileName);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.FileName) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in request.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var started = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {Path.GetFileName(request.FileName)}.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(request.FileName)}: {ex.Message}", ex);
        }

        using var timeoutCts = request.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stdoutLines = new BoundedLineBuffer(request.MaxCapturedLines);
        var stderrLines = new BoundedLineBuffer(request.MaxCapturedLines);

        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutLines, onStdOut, linkedCts.Token);
        var stderrTask = ReadStreamAsync(process.StandardError, stderrLines, onStdErr, linkedCts.Token);

        if (request.StandardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(request.StandardInput);
                process.StandardInput.Close();
            }
            catch
            {
                // The process may have exited before consuming input.
            }
        }

        var timedOut = false;
        var wasCancelled = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            wasCancelled = cancellationToken.IsCancellationRequested;
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore; the process may already be gone.
            }
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Streams can remain open briefly after a killed process.
        }

        started.Stop();
        var exitCode = -1;
        try
        {
            if (process.HasExited) exitCode = process.ExitCode;
        }
        catch
        {
            // Ignore inaccessible exit code.
        }

        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = stdoutLines.ToString(),
            StandardError = stderrLines.ToString(),
            StandardOutputLines = stdoutLines.Lines,
            StandardErrorLines = stderrLines.Lines,
            Duration = started.Elapsed,
            WasCancelled = wasCancelled,
            TimedOut = timedOut,
            ProcessId = process.Id
        };
    }

    public bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void KillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited.
        }
    }

    private static async Task ReadStreamAsync(StreamReader reader, BoundedLineBuffer buffer,
        Action<string>? callback, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line is null) break;
            buffer.Add(line);
            try
            {
                callback?.Invoke(line);
            }
            catch
            {
                // A callback failure must not take down process execution.
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore.
        }
    }

    private sealed class BoundedLineBuffer
    {
        private readonly int _maxLines;
        private readonly Queue<string> _queue = new();

        public BoundedLineBuffer(int maxLines)
        {
            _maxLines = Math.Max(10, maxLines);
        }

        public void Add(string line)
        {
            lock (_queue)
            {
                _queue.Enqueue(line);
                while (_queue.Count > _maxLines) _queue.Dequeue();
            }
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_queue) return _queue.ToList();
            }
        }

        public override string ToString()
        {
            lock (_queue) return string.Join(Environment.NewLine, _queue);
        }
    }
}
