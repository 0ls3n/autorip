namespace AutoRip.Models;

public class Settings
{
    public string OutputDirectory { get; set; } = "~/Videos/Rips";
    public string HandbrakePreset { get; set; } = "Very Fast 1080p30";
    public bool AutoDeleteMkv { get; set; } = true;
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

    public TransferMode PostTransferMode { get; set; } = TransferMode.None;
}
