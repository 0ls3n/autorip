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
    private RipJob? _activeRip;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _ripSignal = new(0);

    public event Action? StateChanged;
    public event Action<RipJob>? JobUpdated;

    private long _lastBytes;
    private DateTime _lastProgressTime;

    public RipJob? ActiveRip
    {
        get { lock (_lock) return _activeRip; }
        private set { lock (_lock) _activeRip = value; }
    }

    public IReadOnlyList<RipJob> ProcessingJobs
    {
        get { lock (_lock) return _processingQueue.ToList(); }
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
        lock (_lock)
        {
            if (_activeRip?.Id == jobId)
            {
                _cts?.Cancel();
                return;
            }
        }
        await Task.CompletedTask;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
        _logger.LogInformation("RipOrchestrator started");
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
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

            while (_pendingRips.TryDequeue(out var item))
            {
                if (ct.IsCancellationRequested) break;
                await ProcessRipAsync(item.Job, item.Device, ct);
            }
        }
    }

    private async Task ProcessRipAsync(RipJob job, string device, CancellationToken ct)
    {
        ActiveRip = job;

        await LogToJobAsync(job.Id, "Info", "Starting rip…");
        await UpdateJobStatusAsync(job, RipStatus.Ripping);

        job.RipStartedAt = DateTime.Now;
        _lastBytes = 0;
        _lastProgressTime = DateTime.Now;

        StateChanged?.Invoke();
        JobUpdated?.Invoke(job);

        await _driveService.PausePollingAsync();

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
                    var deltaSec = (now - _lastProgressTime).TotalSeconds;

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
                        var bytesPerSec = (bytesRead - _lastBytes) / deltaSec;
                        job.RipSpeed = FormatSpeed(bytesPerSec);
                    }

                    _lastBytes = bytesRead;
                    _lastProgressTime = now;

                    StateChanged?.Invoke();
                    JobUpdated?.Invoke(job);
                },
                ct: ct);

            job.MkvPath = mkvPath;
            job.RipProgress = 100;
            job.RipEta = string.Empty;
            job.RipSpeed = string.Empty;

            StateChanged?.Invoke();
            JobUpdated?.Invoke(job);

            await LogToJobAsync(job.Id, "Info", $"Rip complete: {mkvPath}");
            EnqueueProcessing(job);
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
            _driveService.ResumePolling();
            JobUpdated?.Invoke(job);
            _ = DelayClearActiveRip();
        }
    }

    private async Task DelayClearActiveRip()
    {
        await Task.Delay(3000);
        ActiveRip = null;
        StateChanged?.Invoke();
    }

    private void EnqueueProcessing(RipJob job)
    {
        _processingQueue.Enqueue(job);
        _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        while (_processingQueue.TryDequeue(out var job))
        {
            await LogToJobAsync(job.Id, "Info", "Entered processing queue");
            await UpdateJobStatusAsync(job, RipStatus.QueuedForProcessing);
            StateChanged?.Invoke();

            try
            {
                await UpdateJobStatusAsync(job, RipStatus.Transcoding);
                StateChanged?.Invoke();
                JobUpdated?.Invoke(job);

                using var scope = _scopeFactory.CreateScope();
                var handbrake = scope.ServiceProvider.GetRequiredService<HandbrakeService>();

                var preset = string.IsNullOrWhiteSpace(job.HandbrakePreset)
                    ? "Very Fast 1080p30"
                    : job.HandbrakePreset;

                var mp4Path = await handbrake.TranscodeAsync(
                    job.MkvPath!,
                    job.OutputDir,
                    job.MovieName,
                    preset,
                    onProgress: (percent, fps) =>
                    {
                        job.ProcessingProgress = percent;
                        _ = BroadcastAsync(job);
                        JobUpdated?.Invoke(job);
                    },
                    ct: CancellationToken.None);

                job.Mp4Path = mp4Path;
                job.ProcessingProgress = 100;

                if (job.DeleteMkvAfterTranscode && File.Exists(job.MkvPath))
                {
                    try { File.Delete(job.MkvPath); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete mkv: {Path}", job.MkvPath); }
                }

                await UpdateJobStatusAsync(job, RipStatus.Completed);
                job.CompletedAt = DateTime.Now;

                await LogToJobAsync(job.Id, "Info", $"Transcode complete: {mp4Path}");
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
                StateChanged?.Invoke();
                JobUpdated?.Invoke(job);
            }
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
        _cts?.Cancel();
        _cts?.Dispose();
        _ripSignal.Dispose();
        GC.SuppressFinalize(this);
    }
}
