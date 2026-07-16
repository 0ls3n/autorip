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

        var destinations = new List<string>();
        var totalSteps = (mode is TransferMode.Sftp or TransferMode.Both ? 1 : 0)
                       + (mode is TransferMode.LocalCopy or TransferMode.Both ? 1 : 0);
        var step = 0;

        if (mode is TransferMode.Sftp or TransferMode.Both)
        {
            var remote = await UploadSftpAsync(job.Mp4Path, settings, percent =>
            {
                var overall = totalSteps == 1 ? percent : (step + percent / 100.0) / totalSteps * 100.0;
                onProgress?.Invoke(overall, settings.SftpHost ?? string.Empty);
            }, ct);
            destinations.Add($"sftp://{settings.SftpHost}:{settings.SftpPort}{remote}");
            onLog?.Invoke($"Uploaded via SFTP: {remote}");
            step++;
        }
        else
        {
            onProgress?.Invoke(0, string.Empty);
        }

        if (mode is TransferMode.LocalCopy or TransferMode.Both)
        {
            var local = await CopyLocalAsync(job.Mp4Path, settings.LocalCopyPath, percent =>
            {
                var overall = totalSteps == 1 ? percent : (step + percent / 100.0) / totalSteps * 100.0;
                onProgress?.Invoke(overall, "local");
            }, ct);
            destinations.Add($"file://{local}");
            onLog?.Invoke($"Saved local copy: {local}");
        }

        job.TransferPaths = destinations;
        onProgress?.Invoke(100, string.Empty);
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

    private async Task<string> UploadSftpAsync(string filePath, Settings settings, Action<double> onProgress, CancellationToken ct)
    {
        ValidateSftpSettings(settings);
        var conn = BuildSftpConnection(settings);
        var remoteBase = NormalizeRemotePath(settings.SftpRemotePath);
        var fileName = Path.GetFileName(filePath);
        var remoteFile = $"{remoteBase}/{fileName}";

        return await Task.Run(() =>
        {
            using var client = new SftpClient(conn);
            client.Connect();
            try
            {
                EnsureRemoteDirectory(client, remoteBase);
                ct.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(filePath);
                _logger.LogInformation("Uploading {File} to {Host}:{Path} ({Size})", fileName, settings.SftpHost, remoteFile, fileInfo.Length);

                using var stream = File.OpenRead(filePath);
                client.UploadFile(stream, remoteFile, offset =>
                {
                    if (fileInfo.Length > 0)
                        onProgress(offset * 100.0 / fileInfo.Length);
                });
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }

            return remoteFile;
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

    private static async Task<string> CopyLocalAsync(string filePath, string destDir, Action<double> onProgress, CancellationToken ct)
    {
        var expanded = ExpandPath(destDir);
        Directory.CreateDirectory(expanded);

        var dest = Path.Combine(expanded, Path.GetFileName(filePath));
        await using var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var total = src.Length;
        var buffer = new byte[81920];
        int read;
        long written = 0;
        while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await dst.WriteAsync(buffer, 0, read, ct);
            written += read;
            if (total > 0) onProgress(written * 100.0 / total);
        }

        return dest;
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