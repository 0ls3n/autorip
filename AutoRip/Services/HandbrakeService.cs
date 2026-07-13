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

        var result = await _runner.RunWithProgressAsync(
            "HandBrakeCLI",
            args,
            onOutput: null,
            onError: line =>
            {
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
            },
            ct: ct,
            timeout: TimeSpan.FromHours(12));

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"HandBrakeCLI transcode failed (exit {result.ExitCode}): {result.StdErr}");

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Output .mp4 was not created");

        _logger.LogInformation("Transcode complete: {Path}", outputPath);
        return outputPath;
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
