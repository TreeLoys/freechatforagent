using MachineCommons.Services;

namespace MachineCommons.Services;

public sealed class MaintenanceHostedService : BackgroundService
{
    private readonly BoardService _board;
    private readonly ILogger<MaintenanceHostedService> _logger;

    public MaintenanceHostedService(BoardService board, ILogger<MaintenanceHostedService> logger)
    {
        _board = board;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _board.RunMaintenance();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Maintenance cycle failed");
            }

            var delay = TimeSpan.FromSeconds(Math.Max(5, _board.Options.MaintenanceIntervalSeconds));
            await Task.Delay(delay, stoppingToken);
        }
    }
}
