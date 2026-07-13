using System.Text.RegularExpressions;

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
        string preset,
        Action<double, double?>? onProgress = null,
        CancellationToken ct = default)
    {
        var safeName = SanitizeFileName(movieName);
        var outputPath = Path.Combine(outputDir, $"{safeName}.mp4");
        Directory.CreateDirectory(outputDir);

        _logger.LogInformation("Transcoding: {In} → {Out} (preset: {Preset})", inputPath, outputPath, preset);

        var progressRegex = new Regex(@"Encoding: task \d+ of \d+, (\d+\.\d+) %");
        var fpsRegex = new Regex(@"\((\d+\.\d+) fps");

        int lastPercent = -1;

        var result = await _runner.RunWithProgressAsync(
            "HandBrakeCLI",
            $"-i \"{inputPath}\" -o \"{outputPath}\" --preset \"{preset}\" --all-audio --audio-copy-mask ac3,aac,mp3,dts,dtshd,eac3,truehd,flac --audio-fallback ffac3",
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

        _logger.LogInformation("HandBrake exit={Code}", result.ExitCode);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"HandBrakeCLI transcode failed (exit {result.ExitCode}): {result.StdErr}");

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Output .mp4 was not created");

        var size = new FileInfo(outputPath).Length;
        _logger.LogInformation("Transcode complete: {Path} ({Size} bytes)", outputPath, size);
        return outputPath;
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
