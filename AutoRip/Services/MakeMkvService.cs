using System.Text.RegularExpressions;

namespace AutoRip.Services;

public class MakeMkvService
{
    private readonly ProcessRunner _runner;
    private readonly ILogger<MakeMkvService> _logger;

    public MakeMkvService(ProcessRunner runner, ILogger<MakeMkvService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<string> RipTitleAsync(
        string device,
        string outputDir,
        Action<double, long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        var source = $"dev:{device}";
        Directory.CreateDirectory(outputDir);

        _logger.LogInformation("Starting rip: {Source} → {Output}", source, outputDir);

        long lastBytes = 0;
        var progressRegex = new Regex(@"^PRGV:(\d+),(\d+),(\d+)");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = PollOutputSize(outputDir, lastBytes, onProgress, cts.Token);

        var result = await _runner.RunWithProgressAsync(
            "makemkvcon",
            $"mkv {source} 0 \"{outputDir}\" --minlength=180",
            onOutput: line =>
            {
                _logger.LogTrace("mkv stdout: {Line}", line);
                TryParseProgress(line, progressRegex, out var pct, out var cur, out var tot);
                if (pct >= 0)
                    onProgress?.Invoke(pct, cur, tot);
            },
            onError: line =>
            {
                _logger.LogTrace("mkv stderr: {Line}", line);
                TryParseProgress(line, progressRegex, out var pct, out var cur, out var tot);
                if (pct >= 0)
                    onProgress?.Invoke(pct, cur, tot);
            },
            ct: ct,
            timeout: TimeSpan.FromHours(4));

        cts.Cancel();
        try { await pollTask; } catch (OperationCanceledException) { }

        _logger.LogInformation("makemkvcon exit={Code}", result.ExitCode);

        if (result.ExitCode != 0 && result.ExitCode != 253)
            throw new InvalidOperationException($"makemkvcon rip failed (exit {result.ExitCode}): {result.StdErr}");

        var mkvPath = FindLargestMkv(outputDir);
        if (string.IsNullOrEmpty(mkvPath))
            throw new InvalidOperationException("No .mkv file was created. Check that the disc is readable by MakeMKV.");

        var finalSize = new FileInfo(mkvPath).Length;
        onProgress?.Invoke(100, finalSize, finalSize);
        _logger.LogInformation("Rip complete: {Path} ({Size} bytes)", mkvPath, finalSize);
        return mkvPath;
    }

    private static async Task PollOutputSize(
        string outputDir,
        long lastBytes,
        Action<double, long, long>? onProgress,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            long currentBytes = 0;
            try
            {
                if (Directory.Exists(outputDir))
                    currentBytes = Directory.GetFiles(outputDir, "*.mkv", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
            }
            catch { }

            if (currentBytes > lastBytes)
            {
                lastBytes = currentBytes;
                onProgress?.Invoke(-1, currentBytes, 0);
            }
        }
    }

    private static void TryParseProgress(string line, Regex regex, out double percent, out long current, out long total)
    {
        percent = -1;
        current = 0;
        total = 0;

        var match = regex.Match(line);
        if (!match.Success) return;

        current = long.Parse(match.Groups[1].Value);
        total = long.Parse(match.Groups[2].Value);
        var max = long.Parse(match.Groups[3].Value);

        if (total <= 0 && max <= 0) return;

        percent = total > 0
            ? Math.Min(100, (double)current / total * 100)
            : Math.Min(100, (double)current / max * 100);
    }

    private static string? FindLargestMkv(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return null;
        var files = Directory.GetFiles(outputDir, "*.mkv", SearchOption.AllDirectories);
        return files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }
}
