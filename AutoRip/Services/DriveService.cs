using AutoRip.Models;

namespace AutoRip.Services;

public class DriveService : IDisposable
{
    private readonly ProcessRunner _runner;
    private readonly ILogger<DriveService> _logger;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    private readonly Dictionary<string, DiscInfo> _drives = new();
    private bool _hasPolled;
    private readonly Lock _lock = new();

    public event Action<IReadOnlyList<DiscInfo>>? DrivesChanged;

    public bool HasPolled
    {
        get { lock (_lock) return _hasPolled; }
    }

    public IReadOnlyList<DiscInfo> Drives
    {
        get { lock (_lock) return _drives.Values.OrderBy(d => d.DevicePath).ToList(); }
    }

    public DriveService(ProcessRunner runner, ILogger<DriveService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task PausePollingAsync()
    {
        await _pollLock.WaitAsync();
        _logger.LogDebug("Drive polling paused");
    }

    public void ResumePolling()
    {
        try { _pollLock.Release(); } catch (SemaphoreFullException) { }
        _logger.LogDebug("Drive polling resumed");
    }

    public void StartPolling(TimeSpan? interval = null)
    {
        if (_pollingTask != null) return;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(3));
        _pollingTask = PollLoopAsync(_cts.Token);
        _logger.LogInformation("Drive polling started");
    }

    public async Task StopPollingAsync()
    {
        _cts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask; } catch (OperationCanceledException) { }
        }
        _timer?.Dispose();
        _timer = null;
        _cts?.Dispose();
        _cts = null;
        _pollingTask = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (_timer != null && await _timer.WaitForNextTickAsync(ct))
        {
            await PollOnceAsync();
        }
    }

    public async Task PollOnceAsync()
    {
        if (!await _pollLock.WaitAsync(0)) return;

        try
        {
            var devices = FindAllOpticalDevices();
            var changed = false;

            if (devices.Count == 0)
            {
                lock (_lock)
                {
                    _hasPolled = true;
                    if (_drives.Count > 0)
                    {
                        _drives.Clear();
                        changed = true;
                    }
                }

                if (changed || !_hasPolled)
                {
                    _logger.LogDebug("No optical drives found");
                    DrivesChanged?.Invoke(Drives);
                }
                return;
            }

            foreach (var device in devices)
            {
                var model = await ReadDriveModelAsync(device);
                var hasMedia = await CheckMediaAsync(device);

                DiscInfo updated;
                if (hasMedia)
                {
                    var label = await ReadLabelAsync(device);
                    if (string.IsNullOrWhiteSpace(label))
                        label = "Unknown Disc";

                    updated = new DiscInfo
                    {
                        DevicePath = device,
                        DriveModel = model,
                        HasMedia = true,
                        Label = label,
                        IsAmbiguous = true
                    };
                }
                else
                {
                    updated = new DiscInfo
                    {
                        DevicePath = device,
                        DriveModel = model,
                        HasMedia = false
                    };
                }

                lock (_lock)
                {
                    _hasPolled = true;

                    if (_drives.TryGetValue(device, out var existing))
                    {
                        var stateChanged = existing.HasMedia != updated.HasMedia ||
                                           existing.Label != updated.Label;

                        if (!stateChanged)
                        {
                            updated.IsAmbiguous = existing.IsAmbiguous;
                            updated.MovieInfo = existing.MovieInfo;
                            continue;
                        }

                        updated.IsEjecting = existing.IsEjecting;
                        _drives[device] = updated;
                    }
                    else
                    {
                        _drives[device] = updated;
                    }

                    changed = true;
                }
            }

            lock (_lock)
            {
                var removed = _drives.Keys.Where(k => !devices.Contains(k)).ToList();
                foreach (var r in removed)
                {
                    _drives.Remove(r);
                    changed = true;
                }
            }

            if (changed || !_hasPolled)
            {
                _logger.LogTrace("Drives updated: {Count} drives", devices.Count);
                DrivesChanged?.Invoke(Drives);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling drives");
            if (!_hasPolled)
            {
                lock (_lock) _hasPolled = true;
                DrivesChanged?.Invoke(Drives);
            }
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public async Task EjectAsync(string device)
    {
        DiscInfo? drive;
        lock (_lock)
        {
            if (!_drives.TryGetValue(device, out drive)) return;
            drive.IsEjecting = true;
        }

        DrivesChanged?.Invoke(Drives);

        try
        {
            var result = await _runner.RunAsync("eject", device, timeout: TimeSpan.FromSeconds(15));

            if (result.ExitCode != 0)
                _logger.LogWarning("Eject failed for {Device}: {Error}", device, result.StdErr);
            else
                _logger.LogInformation("Ejected {Device}", device);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eject exception for {Device}", device);
        }

        lock (_lock)
        {
            if (_drives.TryGetValue(device, out var existing))
            {
                existing.IsEjecting = false;
                existing.HasMedia = false;
                existing.Label = string.Empty;
                existing.MovieInfo = null;
                existing.IsAmbiguous = false;
            }
        }

        DrivesChanged?.Invoke(Drives);
    }

    public void UpdateDriveMetadata(string device, MovieInfo? info, string customName)
    {
        lock (_lock)
        {
            if (!_drives.TryGetValue(device, out var drive)) return;
            drive.MovieInfo = info;
            drive.IsAmbiguous = info == null;
        }

        DrivesChanged?.Invoke(Drives);
    }

    public async Task CloseTrayAsync(string device)
    {
        try
        {
            await _runner.RunAsync("eject", $"-t \"{device}\"", timeout: TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Close tray failed for {Device}", device);
        }
    }

    private static List<string> FindAllOpticalDevices()
    {
        var devices = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var path = $"/dev/sr{i}";
            if (File.Exists(path))
                devices.Add(path);
        }
        return devices;
    }

    private async Task<string> ReadDriveModelAsync(string device)
    {
        var name = Path.GetFileName(device);

        try
        {
            var modelPath = $"/sys/class/block/{name}/device/model";
            if (File.Exists(modelPath))
                return (await File.ReadAllTextAsync(modelPath)).Trim();
        }
        catch { }

        try
        {
            var result = await _runner.RunAsync("udevadm", $"info --query=property --name=\"{device}\"", timeout: TimeSpan.FromSeconds(3));
            if (result.ExitCode == 0)
            {
                foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("ID_MODEL=", StringComparison.OrdinalIgnoreCase))
                        return line.Split('=', 2)[1].Trim();
                }
            }
        }
        catch { }

        return device;
    }

    private async Task<bool> CheckMediaAsync(string device)
    {
        bool sysfsHasMedia = false;

        try
        {
            var sizePath = $"/sys/class/block/{Path.GetFileName(device)}/size";
            if (File.Exists(sizePath))
            {
                var size = await File.ReadAllTextAsync(sizePath);
                sysfsHasMedia = long.TryParse(size.Trim(), out var sectors) && sectors > 0;
            }
        }
        catch { }

        try
        {
            var result = await _runner.RunAsync("blkid", device, timeout: TimeSpan.FromSeconds(5));
            bool blkidNoMedium = result.ExitCode == 2 ||
                result.StdErr.Contains("no medium found", StringComparison.OrdinalIgnoreCase);

            if (blkidNoMedium)
                return false;

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
                return true;
        }
        catch { }

        return sysfsHasMedia;
    }

    private async Task<string?> ReadLabelAsync(string device)
    {
        try
        {
            var result = await _runner.RunAsync("blkid", $"-s LABEL -o value \"{device}\"", timeout: TimeSpan.FromSeconds(5));
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
                return result.StdOut.Trim();
        }
        catch { }

        try
        {
            var result = await _runner.RunAsync("blkid", $"-o udev \"{device}\"", timeout: TimeSpan.FromSeconds(5));
            if (result.ExitCode == 0)
            {
                foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("ID_FS_LABEL=", StringComparison.OrdinalIgnoreCase))
                        return line.Split('=', 2)[1].Trim();
                    if (line.StartsWith("ID_FS_LABEL_ENC=", StringComparison.OrdinalIgnoreCase))
                        return line.Split('=', 2)[1].Trim();
                }
            }
        }
        catch { }

        try
        {
            var result = await _runner.RunAsync("isoinfo", $"-d -i \"{device}\"", timeout: TimeSpan.FromSeconds(5));
            if (result.ExitCode == 0)
            {
                foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Volume id:", StringComparison.OrdinalIgnoreCase))
                        return line.Split(':', 2)[1].Trim();
                }
            }
        }
        catch { }

        try
        {
            var result = await _runner.RunAsync("volname", device, timeout: TimeSpan.FromSeconds(5));
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
                return result.StdOut.Trim();
        }
        catch { }

        return null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _cts?.Dispose();
        _pollLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
