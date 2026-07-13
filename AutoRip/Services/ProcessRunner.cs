using System.Diagnostics;

namespace AutoRip.Services;

public class ProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public record ProcessResult(int ExitCode, string StdOut, string StdErr);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringWriter();
        var error = new StringWriter();
        var outputTcs = new TaskCompletionSource();
        var errorTcs = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.WriteLine(e.Data);
            else outputTcs.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) error.WriteLine(e.Data);
            else errorTcs.TrySetResult();
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);

            if (ct.CanBeCanceled)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(effectiveTimeout);
                await process.WaitForExitAsync(linked.Token);
            }
            else
            {
                using var cts = new CancellationTokenSource(effectiveTimeout);
                await process.WaitForExitAsync(cts.Token);
            }

            await Task.WhenAll(outputTcs.Task, errorTcs.Task);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }

        var result = new ProcessResult(
            process.ExitCode,
            output.ToString().TrimEnd(),
            error.ToString().TrimEnd());

        _logger.LogTrace("{File} {Args} → exit {Code}", fileName, arguments, result.ExitCode);
        return result;
    }

    public async Task<ProcessResult> RunWithProgressAsync(
        string fileName,
        string arguments,
        Action<string>? onOutput = null,
        Action<string>? onError = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();
        var tcs = new TaskCompletionSource();
        var errTcs = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                onOutput?.Invoke(e.Data);
            }
            else tcs.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
                onError?.Invoke(e.Data);
            }
            else errTcs.TrySetResult();
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(10);

            if (ct.CanBeCanceled)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(effectiveTimeout);
                await process.WaitForExitAsync(linked.Token);
            }
            else
            {
                using var cts = new CancellationTokenSource(effectiveTimeout);
                await process.WaitForExitAsync(cts.Token);
            }

            await Task.WhenAll(tcs.Task, errTcs.Task);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            outputBuilder.ToString().TrimEnd(),
            errorBuilder.ToString().TrimEnd());
    }
}
