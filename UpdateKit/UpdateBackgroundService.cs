using Microsoft.Extensions.Hosting;

namespace Clemar.UpdateKit;

/// <summary>One low-rate background check loop, never blocks application startup.</summary>
public sealed class UpdateBackgroundService(UpdateCoordinator updates) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!updates.Status.CanAutoUpdate) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
            int failures = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await updates.CheckAndStageAsync(stoppingToken);
                var state = updates.Status;
                if (state.Stage == "failed") failures++;
                else failures = 0;
                var delay = failures switch
                {
                    1 => TimeSpan.FromMinutes(1),
                    2 => TimeSpan.FromMinutes(5),
                    3 => TimeSpan.FromMinutes(30),
                    _ => TimeSpan.FromHours(6)
                };
                await Task.Delay(delay,stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

/// <summary>Expose embedded vanilla-JS UI from the reusable library.</summary>
public static class UpdateBannerScript
{
    public static readonly string Content = Load();
    private static string Load()
    {
        using var stream = typeof(UpdateBannerScript).Assembly.GetManifestResourceStream(
            "Clemar.UpdateKit.Web.update-banner.js")
            ?? throw new InvalidOperationException("Update banner resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
