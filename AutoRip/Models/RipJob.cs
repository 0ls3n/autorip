using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace AutoRip.Models;

[Table("RipJobs")]
public class RipJob
{
    [Key]
    [MaxLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [MaxLength(256)]
    public string DiscLabel { get; set; } = string.Empty;

    [MaxLength(256)]
    public string MovieName { get; set; } = string.Empty;

    [MaxLength(512)]
    public string OutputDir { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? MkvPath { get; set; }

    [MaxLength(512)]
    public string? Mp4Path { get; set; }

    public string SubtitlesJson { get; set; } = "[]";

    public RipStatus Status { get; set; } = RipStatus.Ripping;
    public double RipProgress { get; set; }
    public double ProcessingProgress { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public bool DeleteMkvAfterTranscode { get; set; }

    [MaxLength(128)]
    public string HandbrakePreset { get; set; } = string.Empty;

    public TransferMode TransferMode { get; set; } = TransferMode.None;

    public string? MovieInfoJson { get; set; }

    [NotMapped]
    public List<SubtitleResult> Subtitles
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SubtitlesJson)) return new();
            try { return JsonSerializer.Deserialize<List<SubtitleResult>>(SubtitlesJson) ?? new(); }
            catch { return new(); }
        }
        set => SubtitlesJson = JsonSerializer.Serialize(value ?? new());
    }

    [NotMapped]
    public MovieInfo? MovieInfo
    {
        get
        {
            if (string.IsNullOrWhiteSpace(MovieInfoJson)) return null;
            try { return JsonSerializer.Deserialize<MovieInfo>(MovieInfoJson); }
            catch { return null; }
        }
        set => MovieInfoJson = value != null ? JsonSerializer.Serialize(value) : null;
    }

    [NotMapped] public List<string> TransferPaths { get; set; } = new();

    [NotMapped] public DateTime RipStartedAt { get; set; }
    [NotMapped] public string RipElapsed => (DateTime.Now - RipStartedAt).ToString(@"h\:mm\:ss");
    [NotMapped] public string RipEta { get; set; } = string.Empty;
    [NotMapped] public string RipSpeed { get; set; } = string.Empty;
    [NotMapped] public long RipBytesRead { get; set; }
    [NotMapped] public long RipTotalBytes { get; set; }
}

public class SubtitleResult
{
    public string Language { get; set; } = string.Empty;
    public string? SrtPath { get; set; }
    public bool IsSdh { get; set; }
}
