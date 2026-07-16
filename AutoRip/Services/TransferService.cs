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
        CancellationToken ct = default)
    {
        if (job.Mp4Path is null || !File.Exists(job.Mp4Path))
            throw new InvalidOperationException("No transcoded file to transfer.");

        var mode = job.TransferMode;
        if (mode == TransferMode.None) return;

        var destinations = new List<string>();

        if (mode is TransferMode.Sftp or TransferMode.Both)
        {
            var remote = await UploadSftpAsync(job.Mp4Path, settings, ct);
            destinations.Add($"sftp://{settings.SftpHost}:{settings.SftpPort}{remote}");
            onLog?.Invoke($"Uploaded via SFTP: {remote}");
        }

        if (mode is TransferMode.LocalCopy or TransferMode.Both)
        {
            var local = await CopyLocalAsync(job.Mp4Path, settings.LocalCopyPath, ct);
            destinations.Add($"file://{local}");
            onLog?.Invoke($"Saved local copy: {local}");
        }

        job.TransferPaths = destinations;
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

    private async Task<string> UploadSftpAsync(string filePath, Settings settings, CancellationToken ct)
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
                client.UploadFile(stream, remoteFile);
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

    private static async Task<string> CopyLocalAsync(string filePath, string destDir, CancellationToken ct)
    {
        var expanded = ExpandPath(destDir);
        Directory.CreateDirectory(expanded);

        var dest = Path.Combine(expanded, Path.GetFileName(filePath));
        await using var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await src.CopyToAsync(dst, 81920, ct);
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