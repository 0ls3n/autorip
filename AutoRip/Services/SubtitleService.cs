using System.Diagnostics;
using System.Text.Json;
using AutoRip.Models;

namespace AutoRip.Services;

public class SubtitleService
{
    private readonly ProcessRunner _runner;
    private readonly ILogger<SubtitleService> _logger;

    public SubtitleService(ProcessRunner runner, ILogger<SubtitleService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "utf8", "webvtt", "stl", "text"
    };

    private static readonly HashSet<string> VobsubCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dvd_subtitle", "dvd_sub", "vobsub"
    };

    private static readonly HashSet<string> PgsCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "pgs_subtitle", "pgs"
    };

    public async Task<List<SubtitleResult>> ExtractSubtitlesAsync(
        string mkvPath,
        string outputDir,
        string movieName,
        Settings settings,
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
    {
        var results = new List<SubtitleResult>();
        if (!File.Exists(mkvPath))
        {
            onProgress?.Invoke(100, "MKV file not found");
            return results;
        }

        Directory.CreateDirectory(outputDir);

        onProgress?.Invoke(0, "Scanning subtitle tracks…");
        var tracks = await GetSubtitleTracksAsync(mkvPath, ct);

        if (tracks.Count == 0)
        {
            onProgress?.Invoke(100, "No subtitle tracks found");
            _logger.LogInformation("No subtitle tracks in {Path}", mkvPath);
            return results;
        }

        var selected = FilterTracks(tracks, settings);
        if (selected.Count == 0)
        {
            onProgress?.Invoke(100, "No subtitle tracks match filter");
            _logger.LogInformation("No subtitle tracks selected after filtering");
            return results;
        }

        _logger.LogInformation("Found {Count} subtitle track(s), {Selected} selected",
            tracks.Count, selected.Count);

        var safeName = SanitizeFileName(movieName);

        for (int i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = selected[i];
            var lang = string.IsNullOrWhiteSpace(track.Language)
                ? "und"
                : track.Language.ToLowerInvariant();

            var baseName = track.IsSdh ? $"{safeName}.{lang}.sdh" : $"{safeName}.{lang}";
            var srtPath = Path.Combine(outputDir, $"{baseName}.srt");

            double progress = selected.Count == 1 ? 50 : (double)i / selected.Count * 100;
            onProgress?.Invoke(progress, $"Extracting {lang} subtitle ({i + 1}/{selected.Count})…");

            try
            {
                var ok = await ExtractTrackAsync(mkvPath, track, srtPath, settings, ct);

                if (ok && File.Exists(srtPath) && new FileInfo(srtPath).Length > 0)
                {
                    results.Add(new SubtitleResult
                    {
                        Language = lang,
                        SrtPath = srtPath,
                        IsSdh = track.IsSdh
                    });
                    _logger.LogInformation("Extracted subtitle: {Lang} → {Path}", lang, srtPath);
                }
                else if (File.Exists(srtPath) && new FileInfo(srtPath).Length == 0)
                {
                    try { File.Delete(srtPath); } catch { }
                    _logger.LogWarning("Extracted SRT for {Lang} was empty, skipping", lang);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract subtitle {Lang}", lang);
                onProgress?.Invoke(progress, $"Failed to extract {lang}: {ex.Message}");
            }
        }

        onProgress?.Invoke(100, $"Extracted {results.Count} subtitle(s)");
        return results;
    }

    private static readonly HashSet<string> MkvmergeTextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "SubRip", "SubRip/SRT", "SRT",
        "SubStationAlpha", "ASS", "SSA",
        "S_TEXT/UTF8", "S_TEXT/ASCII",
        "mov_text", "UTF-8", "UTF8"
    };

    private async Task<List<SubtitleTrackInfo>> GetSubtitleTracksAsync(string mkvPath, CancellationToken ct)
    {
        // Primary: use mkvmerge (most reliable for MKV language/codec detection)
        var tracks = await GetTracksFromMkvmergeAsync(mkvPath, ct);
        if (tracks.Count > 0)
            return tracks;

        // Fallback: ffprobe (works on non-MKV containers too)
        _logger.LogInformation("mkvmerge returned no tracks, falling back to ffprobe…");
        return await GetTracksFromFfprobeAsync(mkvPath, ct);
    }

    private async Task<List<SubtitleTrackInfo>> GetTracksFromMkvmergeAsync(string mkvPath, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            "mkvmerge",
            $"--identify -J \"{mkvPath}\"",
            ct: ct,
            timeout: TimeSpan.FromSeconds(45));

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            _logger.LogWarning("mkvmerge failed (exit {Code}): {Err}", result.ExitCode, result.StdErr);
            return new List<SubtitleTrackInfo>();
        }

        var tracks = new List<SubtitleTrackInfo>();
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracksArray)
                || tracksArray.ValueKind != JsonValueKind.Array)
                return tracks;

            foreach (var t in tracksArray.EnumerateArray())
            {
                var type = t.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                if (!string.Equals(type, "subtitles", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = t.TryGetProperty("id", out var tid) ? tid.GetInt32() : -1;
                var codec = t.TryGetProperty("codec", out var cd) ? cd.GetString() ?? "" : "";

                var language = string.Empty;
                var title = string.Empty;
                if (t.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    if (props.TryGetProperty("language", out var lang))
                        language = lang.GetString() ?? "";
                    if (props.TryGetProperty("track_name", out var tname))
                        title = tname.GetString() ?? "";
                }

                bool isVobsub = codec.Contains("VobSub", StringComparison.OrdinalIgnoreCase);
                bool isPgs = codec.Contains("PGS", StringComparison.OrdinalIgnoreCase)
                             || codec.Contains("HDMV", StringComparison.OrdinalIgnoreCase);
                bool isText = MkvmergeTextCodecs.Contains(codec)
                              || codec.StartsWith("S_TEXT/", StringComparison.OrdinalIgnoreCase);
                bool isImage = isVobsub || isPgs;

                bool isSdh = title.Contains("SDH", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("Hearing", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("CC", StringComparison.OrdinalIgnoreCase);

                tracks.Add(new SubtitleTrackInfo
                {
                    Index = id,
                    Codec = codec,
                    Language = language,
                    Title = title,
                    IsVobsub = isVobsub,
                    IsPgs = isPgs,
                    IsText = isText,
                    IsImage = isImage,
                    IsSdh = isSdh
                });

                _logger.LogDebug("Subtitle track {Id}: codec={Codec} lang={Lang} text={Text} image={Image}",
                    id, codec, language, isText, isImage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse mkvmerge output");
        }

        for (int i = 0; i < tracks.Count; i++)
            tracks[i].SubtitleStreamIndex = i;

        return tracks;
    }

    private async Task<List<SubtitleTrackInfo>> GetTracksFromFfprobeAsync(string mkvPath, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            "ffprobe",
            $"-v quiet -print_format json -show_streams \"{mkvPath}\"",
            ct: ct,
            timeout: TimeSpan.FromSeconds(45));

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            _logger.LogWarning("ffprobe failed (exit {Code}): {Err}", result.ExitCode, result.StdErr);
            return new List<SubtitleTrackInfo>();
        }

        var tracks = new List<SubtitleTrackInfo>();
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
                return tracks;

            foreach (var s in streams.EnumerateArray())
            {
                var codecType = s.TryGetProperty("codec_type", out var cType) ? cType.GetString() ?? "" : "";
                if (!string.Equals(codecType, "subtitle", StringComparison.OrdinalIgnoreCase))
                    continue;

                var index = s.TryGetProperty("index", out var idx) ? idx.GetInt32() : -1;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                var language = string.Empty;
                var title = string.Empty;

                if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    if (tags.TryGetProperty("language", out var l))
                        language = l.GetString() ?? "";
                    if (tags.TryGetProperty("title", out var t))
                        title = t.GetString() ?? "";
                }

                bool isVobsub = VobsubCodecs.Contains(codec)
                                || codec.Contains("dvd", StringComparison.OrdinalIgnoreCase);
                bool isPgs = PgsCodecs.Contains(codec)
                             || codec.Contains("pgs", StringComparison.OrdinalIgnoreCase);
                bool isText = TextCodecs.Contains(codec);
                bool isImage = isVobsub || isPgs
                               || (!isText && codec.Contains("subtitle", StringComparison.OrdinalIgnoreCase));

                bool isSdh = title.Contains("SDH", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("Hearing", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("CC", StringComparison.OrdinalIgnoreCase);

                tracks.Add(new SubtitleTrackInfo
                {
                    Index = index,
                    Codec = codec,
                    Language = language,
                    Title = title,
                    IsVobsub = isVobsub,
                    IsPgs = isPgs,
                    IsText = isText,
                    IsImage = isImage,
                    IsSdh = isSdh
                });

                _logger.LogDebug("ffprobe subtitle stream {Id}: codec={Codec} lang={Lang} text={Text} image={Image}",
                    index, codec, language, isText, isImage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ffprobe output");
        }

        for (int i = 0; i < tracks.Count; i++)
            tracks[i].SubtitleStreamIndex = i;

        return tracks;
    }

    private static List<SubtitleTrackInfo> FilterTracks(List<SubtitleTrackInfo> tracks, Settings settings)
    {
        if (settings.ExtractAllSubtitles)
            return tracks;

        if (settings.PreferredSubtitleLanguages.Count == 0)
            return tracks;

        var preferred = new HashSet<string>(
            settings.PreferredSubtitleLanguages.Select(l => l.ToLowerInvariant().Trim()),
            StringComparer.OrdinalIgnoreCase);

        return tracks
            .Where(t => string.IsNullOrWhiteSpace(t.Language)
                        || preferred.Contains(t.Language.ToLowerInvariant()))
            .ToList();
    }

    private async Task<bool> ExtractTrackAsync(
        string mkvPath, SubtitleTrackInfo track, string srtPath, Settings settings, CancellationToken ct)
    {
        if (track.IsText)
            return await ExtractTextSubtitleAsync(mkvPath, track, srtPath, ct);

        if (track.IsVobsub)
        {
            if (!settings.OcrVobSub)
            {
                _logger.LogInformation("Skipping VobSub track {Lang} (OCR disabled)", track.Language);
                return false;
            }
            return await OcrVobsubAsync(mkvPath, track, srtPath, settings, ct);
        }

        if (track.IsPgs)
        {
            if (!settings.OcrVobSub)
            {
                _logger.LogInformation("Skipping PGS track {Lang} (OCR disabled)", track.Language);
                return false;
            }
            return await OcrPgsAsync(mkvPath, track, srtPath, settings, ct);
        }

        _logger.LogWarning("Unknown subtitle codec '{Codec}' for track {Lang}", track.Codec, track.Language);
        return false;
    }

    private async Task<bool> ExtractTextSubtitleAsync(
        string mkvPath, SubtitleTrackInfo track, string srtPath, CancellationToken ct)
    {
        var args = $"-y -nostdin -i \"{mkvPath}\" -map 0:s:{track.SubtitleStreamIndex} -c:s srt \"{srtPath}\"";
        var result = await _runner.RunAsync("ffmpeg", args, ct: ct, timeout: TimeSpan.FromMinutes(3));

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("ffmpeg failed to extract text subtitle {Lang}: {Err}",
                track.Language, result.StdErr);
            return false;
        }

        return true;
    }

    private async Task<bool> OcrVobsubAsync(
        string mkvPath, SubtitleTrackInfo track, string srtPath, Settings settings, CancellationToken ct)
    {
        if (!IsToolAvailable("vobsub2srt"))
        {
            _logger.LogWarning("vobsub2srt not available; skipping VobSub OCR for {Lang}", track.Language);
            return false;
        }

        var tempBase = Path.Combine(Path.GetDirectoryName(srtPath)!, $".sub_tmp_{track.Index}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBase);

        try
        {
            var subBase = Path.Combine(tempBase, "sub");

            var extractResult = await _runner.RunAsync(
                "mkvextract",
                $"tracks \"{mkvPath}\" {track.Index}:\"{subBase}.sub\"",
                ct: ct,
                timeout: TimeSpan.FromMinutes(10));

            if (extractResult.ExitCode != 0 || !File.Exists($"{subBase}.sub"))
            {
                _logger.LogWarning("mkvextract failed to extract VobSub: {Err}", extractResult.StdErr);
                return false;
            }

            var lang = string.IsNullOrWhiteSpace(track.Language) ? "eng" : track.Language;
            var ocrArgs = $"--lang {lang} \"{subBase}\"";

            var ocrResult = await _runner.RunAsync(
                "vobsub2srt",
                ocrArgs,
                ct: ct,
                timeout: TimeSpan.FromMinutes(30));

            var producedSrt = $"{subBase}.srt";
            if (!File.Exists(producedSrt))
            {
                _logger.LogWarning("vobsub2srt did not produce SRT for {Lang}: {Err}",
                    track.Language, ocrResult.StdErr);
                return false;
            }

            File.Move(producedSrt, srtPath, overwrite: true);
            return true;
        }
        finally
        {
            try { if (Directory.Exists(tempBase)) Directory.Delete(tempBase, recursive: true); }
            catch { }
        }
    }

    private async Task<bool> OcrPgsAsync(
        string mkvPath, SubtitleTrackInfo track, string srtPath, Settings settings, CancellationToken ct)
    {
        if (IsToolAvailable("pgsrip"))
        {
            return await OcrPgsWithPgsripAsync(mkvPath, track, srtPath, ct);
        }

        if (IsToolAvailable("bdsup2sub") && IsToolAvailable("tesseract"))
        {
            _logger.LogWarning("bdsup2sub pipeline not implemented; install pgsrip for PGS OCR on {Lang}",
                track.Language);
            return false;
        }

        _logger.LogWarning("No PGS OCR tool available (need pgsrip); skipping {Lang}", track.Language);
        return false;
    }

    private async Task<bool> OcrPgsWithPgsripAsync(
        string mkvPath, SubtitleTrackInfo track, string srtPath, CancellationToken ct)
    {
        var tempBase = Path.Combine(Path.GetDirectoryName(srtPath)!, $".pgs_tmp_{track.Index}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBase);

        try
        {
            var supPath = Path.Combine(tempBase, "sub.sup");

            var extractResult = await _runner.RunAsync(
                "mkvextract",
                $"tracks \"{mkvPath}\" {track.Index}:\"{supPath}\"",
                ct: ct,
                timeout: TimeSpan.FromMinutes(10));

            if (extractResult.ExitCode != 0 || !File.Exists(supPath))
            {
                _logger.LogWarning("mkvextract failed to extract PGS: {Err}", extractResult.StdErr);
                return false;
            }

            var lang = string.IsNullOrWhiteSpace(track.Language) ? "eng" : track.Language;
            var args = $"--tesseract-language {lang} \"{supPath}\"";

            var ocrResult = await _runner.RunAsync(
                "pgsrip",
                args,
                ct: ct,
                timeout: TimeSpan.FromMinutes(40));

            // pgsrip writes the SRT next to the .sup with the same base name
            var producedSrt = Path.ChangeExtension(supPath, ".srt");
            if (!File.Exists(producedSrt))
            {
                var srtFiles = Directory.GetFiles(tempBase, "*.srt");
                if (srtFiles.Length > 0) producedSrt = srtFiles[0];
            }

            if (!File.Exists(producedSrt))
            {
                _logger.LogWarning("pgsrip did not produce SRT for {Lang}: {Err}",
                    track.Language, ocrResult.StdErr);
                return false;
            }

            File.Move(producedSrt, srtPath, overwrite: true);
            return true;
        }
        finally
        {
            try { if (Directory.Exists(tempBase)) Directory.Delete(tempBase, recursive: true); }
            catch { }
        }
    }

    private static bool IsToolAvailable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                Arguments = $"which {name}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries))
            .Trim('_', '.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }
}

internal sealed class SubtitleTrackInfo
{
    public int Index { get; set; }
    public string Codec { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsText { get; set; }
    public bool IsVobsub { get; set; }
    public bool IsPgs { get; set; }
    public bool IsImage { get; set; }
    public bool IsSdh { get; set; }
    public int SubtitleStreamIndex { get; set; }
}