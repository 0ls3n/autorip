using System.Collections.Concurrent;
using AutoRip.Hubs;
using AutoRip.Models;
using Microsoft.AspNetCore.SignalR;

namespace AutoRip.Services;

public class RipOrchestrator : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ProgressHub> _hubContext;
    private readonly ILogger<RipOrchestrator> _logger;
    private readonly SettingsService _settings;
    private readonly DriveService _driveService;

    private readonly ConcurrentQueue<(RipJob Job, string Device)> _pendingRips = new();
    private readonly ConcurrentQueue<RipJob> _processingQueue = new();
    private readonly ConcurrentDictionary<string, ActiveRipSession> _activeRips = new();
    private RipJob? _activeProcessing;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _ripSignal = new(0);

    public event Action? StateChanged;
    public event Action<RipJob>? JobUpdated;

    private sealed class ActiveRipSession
    {
        public RipJob Job = null!;
        public string Device = string.Empty;
        public CancellationTokenSource RipCts = new();
        public long LastBytes;
        public DateTime LastProgressTime = DateTime.Now;
    }

    public IReadOnlyList<RipJob> ActiveRips
    {
        get
        {
            lock (_lock)
                return _activeRips.Values
                    .OrderBy(s => s.Job.CreatedAt)
                    .Select(s => s.Job)
                    .ToList();
        }
    }

    public bool IsDeviceRipping(string device)
    {
        lock (_lock) return _activeRips.ContainsKey(device);
    }

    public IReadOnlyList<RipJob> ProcessingJobs
    {
        get { lock (_lock) return _processingQueue.ToList(); }
    }

    public RipJob? ActiveProcessing
    {
        get { lock (_lock) return _activeProcessing; }
        private set { lock (_lock) _activeProcessing = value; }
    }

    public IEnumerable<(RipJob Job, string Device)> PendingRips
    {
        get { lock (_lock) return _pendingRips.ToList(); }
    }

    public RipOrchestrator(
        IServiceScopeFactory scopeFactory,
        IHubContext<ProgressHub> hubContext,
        ILogger<RipOrchestrator> logger,
        SettingsService settings,
        DriveService driveService)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
        _settings = settings;
        _driveService = driveService;
    }

    public async Task<RipJob> EnqueueRipAsync(string device, string movieName, string label, MovieInfo? movieInfo)
    {
        var outputBase = _settings.Current.OutputDirectory;
        outputBase = outputBase.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var outputDir = Path.Combine(outputBase, SanitizeFileName(movieName));

        var job = new RipJob
        {
            DiscLabel = label,
            MovieName = movieName,
            OutputDir = outputDir,
            MovieInfo = movieInfo,
            HandbrakePreset = _settings.Current.HandbrakePreset,
            DeleteMkvAfterTranscode = _settings.Current.AutoDeleteMkv,
            TransferMode = _settings.Current.PostTransferMode,
            CreatedAt = DateTime.Now,
            Status = RipStatus.Ripping
        };

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            db.RipJobs.Add(job);
            await db.SaveChangesAsync();
        }

        _pendingRips.Enqueue((job, device));
        _ripSignal.Release();

        await LogToJobAsync(job.Id, "Info", $"Rip enqueued: '{movieName}' from {device}");
        await BroadcastAsync(job);

        _logger.LogInformation("Rip enqueued: {Movie} ({Device})", movieName, device);
        return job;
    }

    public async Task CancelRipAsync(string jobId)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            var session = _activeRips.Values.FirstOrDefault(s => s.Job.Id == jobId);
            if (session == null) return;
            cts = session.RipCts;
        }
        cts?.Cancel();
        await Task.CompletedTask;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_loopCts.Token);
        _logger.LogInformation("RipOrchestrator started");
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _loopCts?.Cancel();
        _logger.LogInformation("RipOrchestrator stopping");
        return _loopTask ?? Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _ripSignal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var deferred = new List<(RipJob Job, string Device)>();

            while (_pendingRips.TryDequeue(out var item))
            {
                if (ct.IsCancellationRequested) { deferred.Add(item); break; }

                bool canStart;
                lock (_lock)
                {
                    var max = _settings.Current.MaxParallelRips;
                    canStart = !_activeRips.ContainsKey(item.Device);
                    if (canStart && max > 0 && _activeRips.Count >= max) canStart = false;
                }

                if (canStart)
                {
                    _ = ProcessRipAsync(item.Job, item.Device, ct);
                }
                else
                {
                    deferred.Add(item);
                }
            }

            if (deferred.Count > 0)
            {
                foreach (var d in deferred)
                    _pendingRips.Enqueue(d);

                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }

                _ripSignal.Release();
            }
        }
    }

    private async Task ProcessRipAsync(RipJob job, string device, CancellationToken loopCt)
    {
        var session = new ActiveRipSession
        {
            Job = job,
            Device = device,
            RipCts = new CancellationTokenSource(),
            LastBytes = 0,
            LastProgressTime = DateTime.Now
        };

        lock (_lock) _activeRips[device] = session;

        await LogToJobAsync(job.Id, "Info", "Starting rip…");
        await UpdateJobStatusAsync(job, RipStatus.Ripping);

        job.RipStartedAt = DateTime.Now;

        using var ripCt = CancellationTokenSource.CreateLinkedTokenSource(loopCt, session.RipCts.Token);

        StateChanged?.Invoke();
        JobUpdated?.Invoke(job);

        _driveService.SetDeviceBusy(device, true);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mkv = scope.ServiceProvider.GetRequiredService<MakeMkvService>();

            var outputDir = Path.Combine(job.OutputDir, "rip");

            var mkvPath = await mkv.RipTitleAsync(
                device,
                outputDir,
                onProgress: (percent, bytesRead, totalBytes) =>
                {
                    var now = DateTime.Now;
                    var deltaSec = (now - session.LastProgressTime).TotalSeconds;

                    if (percent >= 0)
                    {
                        job.RipProgress = percent;
                        var elapsed = (now - job.RipStartedAt).TotalSeconds;
                        if (percent > 1 && percent < 100 && elapsed > 1)
                        {
                            var ratePerSec = percent / elapsed;
                            var etaSec = (100 - percent) / ratePerSec;
                            job.RipEta = etaSec < 3600
                                ? $"{etaSec / 60:F0}m {etaSec % 60:F0}s"
                                : $"{etaSec / 3600:F0}h {(etaSec % 3600) / 60:F0}m";
                        }
                    }

                    job.RipBytesRead = bytesRead;
                    job.RipTotalBytes = totalBytes;

                    if (deltaSec > 1.0 && bytesRead > 0)
                    {
                        var bytesPerSec = (bytesRead - session.LastBytes) / deltaSec;
                        job.RipSpeed = FormatSpeed(bytesPerSec);
                    }

                    session.LastBytes = bytesRead;
                    session.LastProgressTime = now;

                    StateChanged?.Invoke();
                    JobUpdated?.Invoke(job);
                },
                ct: ripCt.Token);

            job.MkvPath = mkvPath;
            job.RipProgress = 100;
            job.RipEta = string.Empty;
            job.RipSpeed = string.Empty;

            StateChanged?.Invoke();
            JobUpdated?.Invoke(job);

            await LogToJobAsync(job.Id, "Info", $"Rip complete: {mkvPath}");
            EnqueueProcessing(job);

            if (_settings.Current.AutoEjectAfterRip)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    await _driveService.EjectAsync(device);
                });
            }
        }
        catch (OperationCanceledException)
        {
            await UpdateJobStatusAsync(job, RipStatus.Failed, "Cancelled");
            _logger.LogWarning("Rip cancelled: {Movie}", job.MovieName);
        }
        catch (Exception ex)
        {
            await UpdateJobStatusAsync(job, RipStatus.Failed, ex.Message);
            await LogToJobAsync(job.Id, "Error", $"Rip failed: {ex.Message}");
            _logger.LogError(ex, "Rip failed: {Movie}", job.MovieName);
        }
        finally
        {
            session.RipCts.Dispose();
            lock (_lock) _activeRips.TryRemove(device, out _);
            _driveService.SetDeviceBusy(device, false);
            StateChanged?.Invoke();
            JobUpdated?.Invoke(job);
            _ripSignal.Release();
        }
    }

    private int _processingRunning;

    private void EnqueueProcessing(RipJob job)
    {
        _processingQueue.Enqueue(job);
        _ = UpdateJobStatusAsync(job, RipStatus.QueuedForProcessing);
        _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _processingRunning, 1, 0) != 0) return;
        try
        {
            while (_processingQueue.TryDequeue(out var job))
            {
                await LogToJobAsync(job.Id, "Info", "Entered processing queue");

                try
                {
                    ActiveProcessing = job;
                    StateChanged?.Invoke();

                    await UpdateJobStatusAsync(job, RipStatus.Transcoding);
                    StateChanged?.Invoke();
                    JobUpdated?.Invoke(job);

                    using var scope = _scopeFactory.CreateScope();
                    var handbrake = scope.ServiceProvider.GetRequiredService<HandbrakeService>();

                    var mp4Path = await handbrake.TranscodeAsync(
                        job.MkvPath!,
                        job.OutputDir,
                        job.MovieName,
                        _settings.Current,
                        onProgress: (percent, fps) =>
                        {
                            job.ProcessingProgress = percent;
                            JobUpdated?.Invoke(job);
                        },
                        ct: CancellationToken.None);

                    job.Mp4Path = mp4Path;
                    job.ProcessingProgress = 100;

                    await TransferResultAsync(job);

                    await UpdateJobStatusAsync(job, RipStatus.Completed);
                    job.CompletedAt = DateTime.Now;

                    await LogToJobAsync(job.Id, "Info", $"Transcode complete: {mp4Path}");
                    if (job.TransferPaths.Count > 0)
                        await LogToJobAsync(job.Id, "Info", "Transferred to: " + string.Join(", ", job.TransferPaths));
                    _logger.LogInformation("Job completed: {Movie}", job.MovieName);
                }
                catch (Exception ex)
                {
                    await UpdateJobStatusAsync(job, RipStatus.Failed, ex.Message);
                    await LogToJobAsync(job.Id, "Error", $"Processing failed: {ex.Message}");
                    _logger.LogError(ex, "Processing failed: {Movie}", job.MovieName);
                }
                finally
                {
                    ActiveProcessing = null;
                    StateChanged?.Invoke();
                    JobUpdated?.Invoke(job);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _processingRunning, 0);
        }
    }

    private async Task UpdateJobStatusAsync(RipJob job, RipStatus status, string? error = null)
    {
        job.Status = status;
        if (error != null) job.ErrorMessage = error;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        db.RipJobs.Update(job);
        await db.SaveChangesAsync();
    }

    private async Task LogToJobAsync(string jobId, string level, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        db.RipLogs.Add(new Data.RipLogEntry
        {
            RipJobId = jobId,
            Timestamp = DateTime.Now,
            Level = level,
            Message = message
        });
        await db.SaveChangesAsync();
    }

    private async Task TransferResultAsync(RipJob job)
    {
        if (job.TransferMode == TransferMode.None)
        {
            if (job.DeleteMkvAfterTranscode && File.Exists(job.MkvPath))
                TryDeleteLocal(job.MkvPath);
            return;
        }

        await UpdateJobStatusAsync(job, RipStatus.Transferring);
        job.TransferProgress = 0;
        job.TransferTarget = _settings.Current.SftpHost ?? "local";
        StateChanged?.Invoke();
        JobUpdated?.Invoke(job);

        using var scope = _scopeFactory.CreateScope();
        var transfer = scope.ServiceProvider.GetRequiredService<TransferService>();

        await transfer.TransferAsync(
            job,
            _settings.Current,
            onLog: msg => _ = LogToJobAsync(job.Id, "Info", msg),
            onProgress: (percent, target) =>
            {
                job.TransferProgress = percent;
                if (!string.IsNullOrEmpty(target)) job.TransferTarget = target;
                JobUpdated?.Invoke(job);
            },
            ct: CancellationToken.None);

        job.TransferProgress = 100;

        foreach (var path in new[] { job.Mp4Path, job.MkvPath })
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                TryDeleteLocal(path);

        CleanupEmptyDirectories(job);

        await LogToJobAsync(job.Id, "Info", "Removed local intermediates from working directory.");

        StateChanged?.Invoke();
        JobUpdated?.Invoke(job);
    }

    private void TryDeleteLocal(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete local file: {Path}", path); }
    }

    private void CleanupEmptyDirectories(RipJob job)
    {
        if (string.IsNullOrEmpty(job.OutputDir)) return;

        var ripDir = Path.Combine(job.OutputDir, "rip");
        TryDeleteIfEmpty(ripDir);
        TryDeleteIfEmpty(job.OutputDir);
    }

    private void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
            Directory.Delete(dir);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up empty directory: {Path}", dir); }
    }

    private async Task BroadcastAsync(RipJob job)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("JobUpdate", new
            {
                job.Id,
                job.MovieName,
                job.DiscLabel,
                Status = job.Status.ToString(),
                job.RipProgress,
                job.ProcessingProgress,
                job.ErrorMessage,
                job.MkvPath,
                job.Mp4Path,
                job.CreatedAt,
                job.CompletedAt
            });
        }
        catch { }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return string.Empty;
        return bytesPerSec switch
        {
            < 1024 => $"{bytesPerSec:F0} B/s",
            < 1024 * 1024 => $"{bytesPerSec / 1024:F1} KB/s",
            < 1024 * 1024 * 1024 => $"{bytesPerSec / (1024 * 1024):F1} MB/s",
            _ => $"{bytesPerSec / (1024 * 1024 * 1024):F2} GB/s"
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim('_', '.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "Unknown";
        return sanitized;
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _ripSignal.Dispose();
        GC.SuppressFinalize(this);
    }
}