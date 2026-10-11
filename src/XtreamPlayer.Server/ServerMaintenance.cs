namespace XtreamPlayer.Server;

/// <summary>Independent of long-running downloads, so abandoned players are always reaped.</summary>
public sealed class ServerMaintenance(MediaEngine media, JobService jobs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                media.Reap();
                try { jobs.EnsureSpace(); }
                catch (InvalidOperationException)
                {
                    jobs.StorageFull();
                    media.StopAll();
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
