using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AutoRip.Data;
using AutoRip.Models;

namespace AutoRip.Services;

public class RipHistoryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RipHistoryService> _logger;

    public RipHistoryService(IServiceScopeFactory scopeFactory, ILogger<RipHistoryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<RipJob> CreateJobAsync(string discLabel, string movieName, string outputDir)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new RipJob
        {
            DiscLabel = discLabel,
            MovieName = movieName,
            OutputDir = outputDir,
            CreatedAt = DateTime.Now,
            Status = RipStatus.Ripping
        };

        db.RipJobs.Add(job);
        await db.SaveChangesAsync();

        await AddLogAsync(job.Id, "Info", $"Rip job created for '{movieName}' (disc: '{discLabel}')");

        return job;
    }

    public async Task UpdateJobAsync(RipJob job)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.RipJobs.Update(job);
        await db.SaveChangesAsync();
    }

    public async Task UpdateJobStatusAsync(string jobId, RipStatus status, string? errorMessage = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.RipJobs.FindAsync(jobId);
        if (job == null) return;

        var oldStatus = job.Status;
        job.Status = status;
        job.ErrorMessage = errorMessage;

        if (status == RipStatus.Completed || status == RipStatus.Failed)
            job.CompletedAt = DateTime.Now;

        await db.SaveChangesAsync();

        await AddLogAsync(jobId, status == RipStatus.Failed ? "Error" : "Info",
            $"Status changed: {oldStatus} → {status}{(errorMessage != null ? $" ({errorMessage})" : "")}");
    }

    public async Task<List<RipJob>> GetRecentJobsAsync(int count = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.RipJobs
            .OrderByDescending(j => j.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<RipJob>> GetAllJobsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.RipJobs
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<RipLogEntry>> GetLogsForJobAsync(string jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.RipLogs
            .Where(l => l.RipJobId == jobId)
            .OrderBy(l => l.Timestamp)
            .ToListAsync();
    }

    public async Task AddLogAsync(string jobId, string level, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.RipLogs.Add(new RipLogEntry
        {
            RipJobId = jobId,
            Timestamp = DateTime.Now,
            Level = level,
            Message = message
        });

        await db.SaveChangesAsync();
        _logger.LogInformation("[{Level}] Job {JobId}: {Message}", level, jobId, message);
    }

    public async Task DeleteJobAsync(string jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var logs = await db.RipLogs.Where(l => l.RipJobId == jobId).ToListAsync();
        db.RipLogs.RemoveRange(logs);

        var job = await db.RipJobs.FindAsync(jobId);
        if (job != null)
            db.RipJobs.Remove(job);

        await db.SaveChangesAsync();
    }
}
