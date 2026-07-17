using AutoRip.Models;
using Renci.SshNet;

namespace AutoRip.Services;

public class TransferService
{
    private readonly ILogger<TransferService> _logger;

    public TransferService(ILogger<TransferService> logger)
    {
        _logger = logger;
    }

    public async Task TransferAsync(
        RipJob job,
        Settings settings,
        Action<string>? onLog = null,
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (job.Mp4Path is null || !File.Exists(job.Mp4Path))
            throw new InvalidOperationException("No transcoded file to transfer.");

        var mode = job.TransferMode;
        if (mode == TransferMode.None) return;

        var files = GetFilesToTransfer(job);
        if (files.Count == 0)
            throw new InvalidOperationException("No files available to transfer.");

        var movieFolder = SanitizeFileName(job.MovieName);

        var destinations = new List<string>();
        var totalSteps = (mode is TransferMode.Sftp or TransferMode.Both ? 1 : 0)
                       + (mode is TransferMode.LocalCopy or TransferMode.Both ? 1 : 0);
        var step = 0;

        if (mode is TransferMode.Sftp or TransferMode.Both)
        {
            var remoteFolder = await UploadFolderSftpAsync(
                files, movieFolder, settings, onLog, percent =>
                {
                    var overall = totalSteps == 1 ? percent : (step + percent / 100.0) / totalSteps * 100.0;
                    onProgress?.Invoke(overall, settings.SftpHost ?? string.Empty);
                }, ct);
            destinations.Add($"sftp://{settings.SftpHost}:{settings.SftpPort}{remoteFolder}");
            onLog?.Invoke($"Uploaded folder via SFTP: {remoteFolder} ({files.Count} file(s))");
            step++;
        }
        else
        {
            onProgress?.Invoke(0, string.Empty);
        }

        if (mode is TransferMode.LocalCopy or TransferMode.Both)
        {
            var localFolder = await CopyFolderLocalAsync(
                files, movieFolder, settings.LocalCopyPath, percent =>
                {
                    var overall = totalSteps == 1 ? percent : (step + percent / 100.0) / totalSteps * 100.0;
                    onProgress?.Invoke(overall, "local");
                }, ct);
            destinations.Add($"file://{localFolder}");
            onLog?.Invoke($"Saved local copy: {localFolder} ({files.Count} file(s))");
        }

        job.TransferPaths = destinations;
        onProgress?.Invoke(100, string.Empty);
    }

    private static List<string> GetFilesToTransfer(RipJob job)
    {
        var files = new List<string>();

        if (!string.IsNullOrEmpty(job.Mp4Path) && File.Exists(job.Mp4Path))
            files.Add(job.Mp4Path);

        foreach (var sub in job.Subtitles)
        {
            if (!string.IsNullOrEmpty(sub.SrtPath) && File.Exists(sub.SrtPath))
                files.Add(sub.SrtPath);
        }

        return files;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries))
            .Trim('_', '.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    public async Task<IReadOnlyList<string>> ListSftpDirectoriesAsync(string remotePath, Settings settings, CancellationToken ct = default)
    {
        ValidateSftpSettings(settings);
        var conn = BuildSftpConnection(settings);
        var normalized = NormalizeRemotePath(remotePath);
        var root = normalized.Length == 0 ? "/" : normalized;

        return await Task.Run(() =>
        {
            using var client = new SftpClient(conn);
            client.Connect();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!client.Exists(root))
                    throw new InvalidOperationException($"Remote path '{root}' does not exist.");

                var dirs = new List<string>();
                foreach (var entry in client.ListDirectory(root))
                {
                    if (entry.IsDirectory && entry.Name is not "." and not "..")
                        dirs.Add(entry.Name);
                }

                dirs.Sort(StringComparer.Ordinal);
                return (IReadOnlyList<string>)dirs;
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }, ct);
    }

    public Task<(string Path, IReadOnlyList<string> Dirs, IReadOnlyList<string> Special)> ResolveLocalAsync(string path)
    {
        return Task.Run(() =>
        {
            var expanded = ExpandDisplayPath(path);
            var special = EnumerateDrives();
            var dirs = new List<string>();

            if (!string.IsNullOrEmpty(expanded))
            {
                try
                {
                    var info = new DirectoryInfo(expanded);
                    if (info.Exists)
                    {
                        foreach (var sub in info.EnumerateDirectories())
                        {
                            try { dirs.Add(sub.Name); }
                            catch (UnauthorizedAccessException) { /* skip */ }
                            catch (IOException) { /* skip */ }
                        }
                        dirs.Sort(StringComparer.Ordinal);
                    }
                }
                catch (UnauthorizedAccessException) { /* treat as no access */ }
                catch (IOException) { /* treat as missing */ }
            }

            return (expanded, (IReadOnlyList<string>)dirs, (IReadOnlyList<string>)special);
        });
    }

    private static IReadOnlyList<string> EnumerateDrives()
    {
        var drives = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady) drives.Add(d.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
        return drives;
    }

    private static string ExpandDisplayPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var expanded = ExpandPath(path);
        if (Directory.Exists(expanded)) return Path.GetFullPath(expanded);

        var parent = Directory.GetParent(expanded);
        while (parent != null && !parent.Exists)
            parent = parent.Parent;
        return parent?.FullName ?? string.Empty;
    }

    public async Task<string> TestSftpConnectionAsync(Settings settings, CancellationToken ct = default)
    {
        ValidateSftpSettings(settings);
        var conn = BuildSftpConnection(settings);
        var remoteBase = NormalizeRemotePath(settings.SftpRemotePath);

        return await Task.Run(() =>
        {
            using var client = new SftpClient(conn);
            client.Connect();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (remoteBase != "/")
                {
                    EnsureRemoteDirectory(client, remoteBase);
                    if (!client.Exists(remoteBase))
                        throw new InvalidOperationException($"Remote path '{remoteBase}' does not exist and could not be created.");
                }
                return remoteBase;
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }, ct);
    }

    private async Task<string> UploadFolderSftpAsync(
        IReadOnlyList<string> files,
        string folderName,
        Settings settings,
        Action<string>? onLog,
        Action<double> onProgress,
        CancellationToken ct)
    {
        ValidateSftpSettings(settings);
        var conn = BuildSftpConnection(settings);
        var remoteBase = NormalizeRemotePath(settings.SftpRemotePath);
        var remoteFolder = $"{remoteBase}/{folderName}";

        var totalBytes = files.Sum(f => new FileInfo(f).Length);
        long uploadedBytes = 0;

        return await Task.Run(() =>
        {
            using var client = new SftpClient(conn);
            client.Connect();
            try
            {
                EnsureRemoteDirectory(client, remoteFolder);
                ct.ThrowIfCancellationRequested();

                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var localPath = files[i];
                    var fileName = Path.GetFileName(localPath);
                    var remoteFile = $"{remoteFolder}/{fileName}";
                    var fileInfo = new FileInfo(localPath);

                    _logger.LogInformation("Uploading {File} to {Host}:{Path} ({Size})",
                        fileName, settings.SftpHost, remoteFile, fileInfo.Length);
                    onLog?.Invoke($"Uploading {fileName} ({i + 1}/{files.Count})…");

                    using var stream = File.OpenRead(localPath);
                    client.UploadFile(stream, remoteFile, offset =>
                    {
                        if (fileInfo.Length > 0 && totalBytes > 0)
                        {
var current = uploadedBytes + (long)offset;
                        onProgress(Math.Min(100, current * 100.0 / totalBytes));
                        }
                    });

                    uploadedBytes += fileInfo.Length;
                }
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }

            return remoteFolder;
        }, ct);
    }

    private static void ValidateSftpSettings(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SftpHost))
            throw new InvalidOperationException("Host is required.");
        if (string.IsNullOrWhiteSpace(settings.SftpUser))
            throw new InvalidOperationException("Username is required.");
        if (string.IsNullOrWhiteSpace(settings.SftpPassword)
            && (string.IsNullOrWhiteSpace(settings.SftpKeyFile) || !File.Exists(settings.SftpKeyFile)))
            throw new InvalidOperationException("Password or a valid SSH key file is required.");
    }

    private static Renci.SshNet.ConnectionInfo BuildSftpConnection(Settings settings)
    {
        var port = settings.SftpPort > 0 ? settings.SftpPort : 22;
        return new Renci.SshNet.ConnectionInfo(
            settings.SftpHost!, port, settings.SftpUser!,
            ResolveAuthMethod(settings));
    }

    private static AuthenticationMethod ResolveAuthMethod(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SftpKeyFile) && File.Exists(settings.SftpKeyFile))
        {
            var keyFile = new PrivateKeyFile(settings.SftpKeyFile!);
            return new PrivateKeyAuthenticationMethod(settings.SftpUser!, keyFile);
        }

        return new PasswordAuthenticationMethod(settings.SftpUser!, settings.SftpPassword ?? string.Empty);
    }

    private static string NormalizeRemotePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "/" : path.TrimEnd('/');

    private static void EnsureRemoteDirectory(SftpClient client, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath) || remotePath == "/") return;

        var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!client.Exists(current))
                client.CreateDirectory(current);
        }
    }

    private static async Task<string> CopyFolderLocalAsync(
        IReadOnlyList<string> files,
        string folderName,
        string destBase,
        Action<double> onProgress,
        CancellationToken ct)
    {
        var expanded = ExpandPath(destBase);
        var destFolder = Path.Combine(expanded, folderName);
        Directory.CreateDirectory(destFolder);

        var totalBytes = files.Sum(f => new FileInfo(f).Length);
        long copiedBytes = 0;
        var buffer = new byte[81920];

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var localPath = files[i];
            var destPath = Path.Combine(destFolder, Path.GetFileName(localPath));

            await using var src = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read, ct);
                copiedBytes += read;
                if (totalBytes > 0)
                    onProgress(Math.Min(100, copiedBytes * 100.0 / totalBytes));
            }
        }

        return destFolder;
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
if (path.StartsWith("~"))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.Substring(1).TrimStart('/'));
        return Path.GetFullPath(path);
    }
}