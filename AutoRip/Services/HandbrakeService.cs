using System.Text.RegularExpressions;
using AutoRip.Models;

namespace AutoRip.Services;

public class HandbrakeService
{
    private readonly ProcessRunner _runner;
    private readonly ILogger<HandbrakeService> _logger;

    public HandbrakeService(ProcessRunner runner, ILogger<HandbrakeService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);
    private const int StallCheckIntervalMs = 30000;
    private const int RecentLineBufferSize = 60;

    public async Task<string> TranscodeAsync(
        string inputPath,
        string outputDir,
        string movieName,
        Settings settings,
        Action<double, double?>? onProgress = null,
        CancellationToken ct = default)
    {
        var safeName = SanitizeFileName(movieName);
        var outputPath = Path.Combine(outputDir, $"{safeName}.mp4");
        Directory.CreateDirectory(outputDir);

        var args = BuildArgs(inputPath, outputPath, settings);
        _logger.LogInformation("Transcoding: {In} → {Out} {Args}", inputPath, outputPath, args);

        var progressRegex = new Regex(@"Encoding: task \d+ of \d+, (\d+\.\d+) %");
        var fpsRegex = new Regex(@"\((\d+\.\d+) fps");
        int lastPercent = -1;

        void HandleLine(string line)
        {
            _logger.LogTrace("HandBrakeCLI: {Line}", line);
            AppendRecent(line);

            var match = progressRegex.Match(line);
            if (match.Success)
            {
                var percent = double.Parse(match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                var pct = (int)percent;
                if (pct != lastPercent)
                {
                    lastPercent = pct;
                    double? fps = null;
                    var fpsMatch = fpsRegex.Match(line);
                    if (fpsMatch.Success)
                        fps = double.Parse(fpsMatch.Groups[1].Value,
                            System.Globalization.CultureInfo.InvariantCulture);
                    onProgress?.Invoke(percent, fps);
                }
            }
        }
        return await RunTranscodeWithStallGuardAsync(inputPath, outputPath, args, HandleLine, ct);
    }

    private async Task<string> RunTranscodeWithStallGuardAsync(
        string inputPath,
        string outputPath,
        string args,
        Action<string> handleLine,
        CancellationToken ct)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stallCts.CancelAfter(TimeSpan.FromHours(12));

        var stallWatcher = WatchOutputStallAsync(outputPath, stallCts.Token);

        ProcessRunner.ProcessResult result;
        try
        {
            result = await _runner.RunWithProgressAsync(
                "HandBrakeCLI",
                args,
                onOutput: handleLine,
                onError: handleLine,
                ct: stallCts.Token,
                timeout: TimeSpan.FromHours(12));
        }
        catch (OperationCanceledException) when (stallCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"HandBrakeCLI transcode stalled (output file unchanged for {StallTimeout.TotalMinutes:F0} min). " +
                $"Recent output:\n{GetRecentOutput()}");
        }
        finally
        {
            stallCts.Cancel();
            try { await stallWatcher; } catch (OperationCanceledException) { }
        }

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"HandBrakeCLI transcode failed (exit {result.ExitCode}): {result.StdErr}" +
                $"\nRecent output:\n{GetRecentOutput()}");

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Output .mp4 was not created");

        _logger.LogInformation("Transcode complete: {Path}", outputPath);
        return outputPath;
    }

    private async Task WatchOutputStallAsync(string outputPath, CancellationToken ct)
    {
        long lastSize = -1;
        DateTime lastChange = DateTime.Now;

        await Task.Delay(StallCheckIntervalMs, ct).ContinueWith(_ => { }, ct, TaskContinuationOptions.OnlyOnCanceled, TaskScheduler.Default);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                long currentSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                if (currentSize > lastSize)
                {
                    lastSize = currentSize;
                    lastChange = DateTime.Now;
                }
                else if (DateTime.Now - lastChange >= StallTimeout)
                {
                    _logger.LogWarning("Transcode stall detected: {Path} unchanged for {Min} min", outputPath, StallTimeout.TotalMinutes);
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogTrace(ex, "Stall watcher error"); }

            try { await Task.Delay(StallCheckIntervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private readonly object _recentLock = new();
    private readonly LinkedList<string> _recentLines = new();

    private void AppendRecent(string line)
    {
        lock (_recentLock)
        {
            _recentLines.AddLast(line);
            while (_recentLines.Count > RecentLineBufferSize)
                _recentLines.RemoveFirst();
        }
    }

    private string GetRecentOutput()
    {
        lock (_recentLock)
            return string.Join("\n", _recentLines);
    }

    private static string BuildArgs(string input, string output, Settings settings)
    {
        var audio = "--all-audio --audio-copy-mask ac3,aac,mp3,dts,dtshd,eac3,truehd,flac --audio-fallback ffac3";

        string coreArgs;
        if (settings.UseCustomHandbrake)
        {
            var encoder = settings.HandbrakeEncoder ?? "x264";
            var quality = settings.HandbrakeQuality;
            var speed = settings.HandbrakeSpeed ?? "veryfast";
            coreArgs = $"-e {encoder} -q {quality:F1} --encoder-preset {speed}";
        }
        else
        {
            var preset = string.IsNullOrWhiteSpace(settings.HandbrakePreset)
                ? "Very Fast 1080p30"
                : settings.HandbrakePreset;
            coreArgs = $"--preset \"{preset}\"";
        }

        var flags = new List<string>();

        if (settings.HandbrakeWebOptimized)
            flags.Add("-O");
        else
            flags.Add("--no-optimize");

        if (settings.HandbrakeAlignAv)
            flags.Add("--align-av");

        if (settings.HandbrakeMarkers)
            flags.Add("-m");
        else
            flags.Add("--no-markers");

        if (!string.IsNullOrWhiteSpace(settings.HandbrakeFramerate) &&
            settings.HandbrakeFramerate != "source")
            flags.Add($"-r {settings.HandbrakeFramerate}");

        if (settings.HandbrakeCfr)
            flags.Add("--cfr");
        else
            flags.Add("--vfr");

        var extra = string.Join(" ", flags);
        return $"-i \"{input}\" -o \"{output}\" {coreArgs} {audio} {extra}";
    }

    public async Task<List<string>> GetPresetsAsync()
    {
        var presets = new List<string>();
        try
        {
            var result = await _runner.RunAsync("HandBrakeCLI", "--preset-list", timeout: TimeSpan.FromSeconds(10));
            foreach (var line in (result.StdOut + "\n" + result.StdErr).Split('\n'))
            {
                if (line.StartsWith("    ") && !line.StartsWith("        "))
                {
                    var name = line.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !name.EndsWith('/'))
                        presets.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list HandBrake presets");
        }

        if (presets.Count == 0)
            presets.Add("Very Fast 1080p30");

        return presets;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries))
            .Trim('_', '.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }
}
