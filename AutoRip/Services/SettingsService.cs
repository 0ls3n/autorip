using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AutoRip.Data;
using AutoRip.Models;

namespace AutoRip.Services;

public class SettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SettingsService> _logger;

    public Settings Current { get; private set; } = new();

    public SettingsService(IServiceScopeFactory scopeFactory, ILogger<SettingsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Current = new Settings();
        var entries = await db.Settings.ToListAsync();

        foreach (var entry in entries)
        {
            switch (entry.Key)
            {
                case "OutputDirectory": Current.OutputDirectory = entry.Value ?? Current.OutputDirectory; break;
                case "HandbrakePreset": Current.HandbrakePreset = entry.Value ?? Current.HandbrakePreset; break;
                case "AutoDeleteMkv": Current.AutoDeleteMkv = entry.Value == "true"; break;
                case "ExtractAllSubtitles": Current.ExtractAllSubtitles = entry.Value == "true"; break;
                case "PreferredSubtitleLanguages":
                    if (!string.IsNullOrWhiteSpace(entry.Value))
                        Current.PreferredSubtitleLanguages = entry.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "OcrVobSub": Current.OcrVobSub = entry.Value == "true"; break;
                case "TmdbApiKey": Current.TmdbApiKey = string.IsNullOrWhiteSpace(entry.Value) ? null : entry.Value; break;
                case "UseTmdbAutoDetect": Current.UseTmdbAutoDetect = entry.Value != "false"; break;
                case "SftpHost": Current.SftpHost = entry.Value; break;
                case "SftpPort": Current.SftpPort = int.TryParse(entry.Value, out var port) ? port : 22; break;
                case "SftpUser": Current.SftpUser = entry.Value; break;
                case "SftpPassword": Current.SftpPassword = entry.Value; break;
                case "SftpKeyFile": Current.SftpKeyFile = entry.Value; break;
                case "SftpRemotePath": Current.SftpRemotePath = entry.Value ?? "/media/"; break;
                case "PostTransferMode":
                    Current.PostTransferMode = entry.Value switch
                    {
                        "Sftp" => TransferMode.Sftp,
                        "LocalCopy" => TransferMode.LocalCopy,
                        "Both" => TransferMode.Both,
                        _ => TransferMode.None
                    };
                    break;
            }
        }

        _logger.LogInformation("Settings loaded, {Count} entries found", entries.Count);
    }

    public async Task SaveAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entries = new Dictionary<string, string?>
        {
            ["OutputDirectory"] = Current.OutputDirectory,
            ["HandbrakePreset"] = Current.HandbrakePreset,
            ["AutoDeleteMkv"] = Current.AutoDeleteMkv.ToString().ToLower(),
            ["ExtractAllSubtitles"] = Current.ExtractAllSubtitles.ToString().ToLower(),
            ["PreferredSubtitleLanguages"] = string.Join(",", Current.PreferredSubtitleLanguages),
            ["OcrVobSub"] = Current.OcrVobSub.ToString().ToLower(),
            ["TmdbApiKey"] = Current.TmdbApiKey,
            ["UseTmdbAutoDetect"] = Current.UseTmdbAutoDetect.ToString().ToLower(),
            ["SftpHost"] = Current.SftpHost,
            ["SftpPort"] = Current.SftpPort.ToString(),
            ["SftpUser"] = Current.SftpUser,
            ["SftpPassword"] = Current.SftpPassword,
            ["SftpKeyFile"] = Current.SftpKeyFile,
            ["SftpRemotePath"] = Current.SftpRemotePath,
            ["PostTransferMode"] = Current.PostTransferMode.ToString()
        };

        foreach (var kvp in entries)
        {
            var existing = await db.Settings.FindAsync(kvp.Key);
            if (existing != null)
            {
                existing.Value = kvp.Value;
            }
            else
            {
                db.Settings.Add(new SettingEntity { Key = kvp.Key, Value = kvp.Value });
            }
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Settings saved, {Count} entries written", entries.Count);
    }
}
