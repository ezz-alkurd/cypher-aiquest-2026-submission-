using CypherCommerce.Data;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Services;

// Disabled until the migration is installed. This worker records durable recovery work;
// it deliberately never reruns an Agent or resends a provider-uncertain delivery.
public sealed class BotRecoveryService(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<BotRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Automation:RecoveryEnabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
                await db.Database.ExecuteSqlRawAsync("EXEC dbo.CypherSweepBotRecovery", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Log neither exception connection details nor customer content.
                logger.LogError("Cypher bot recovery sweep failed; inspect database availability and migrations.");
            }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
