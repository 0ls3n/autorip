namespace AutoRip.Models;

public class Settings
{
    public string OutputDirectory { get; set; } = "~/Videos/Rips";
    public string HandbrakePreset { get; set; } = "Very Fast 1080p30";
    public bool UseCustomHandbrake { get; set; } = true;
    public string? HandbrakeEncoder { get; set; } = "x264";
    public double HandbrakeQuality { get; set; } = 22.0;
    public string? HandbrakeSpeed { get; set; } = "veryfast";
    public bool HandbrakeWebOptimized { get; set; } = true;
    public bool HandbrakeAlignAv { get; set; } = true;
    public bool HandbrakeMarkers { get; set; } = true;
    public string? HandbrakeFramerate { get; set; } = "source";
    public bool HandbrakeCfr { get; set; } = false;
    public bool HandbrakeAutoCrop { get; set; } = false;
    public bool AutoDeleteMkv { get; set; } = true;
    public bool AutoEjectAfterRip { get; set; } = true;
    public bool AutoStartRip { get; set; } = false;
    public int MaxParallelRips { get; set; } = 0;
    public bool ExtractAllSubtitles { get; set; } = true;
    public List<string> PreferredSubtitleLanguages { get; set; } = new() { "eng" };
    public bool OcrVobSub { get; set; } = false;

    public string? TmdbApiKey { get; set; }
    public bool UseTmdbAutoDetect { get; set; } = true;

    public string? SftpHost { get; set; }
    public int SftpPort { get; set; } = 22;
    public string? SftpUser { get; set; }
    public string? SftpPassword { get; set; }
    public string? SftpKeyFile { get; set; }
    public string SftpRemotePath { get; set; } = "/media/";

    public string LocalCopyPath { get; set; } = "~/Videos/Converted";

    public TransferMode PostTransferMode { get; set; } = TransferMode.None;
}
