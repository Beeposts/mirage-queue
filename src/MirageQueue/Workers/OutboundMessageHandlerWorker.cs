﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirageQueue.Consumers.Abstractions;

namespace MirageQueue.Workers;

public abstract class OutboundMessageHandlerWorker(
    IServiceProvider serviceProvider,
    ILogger<OutboundMessageHandlerWorker> logger,
    MirageQueueConfiguration configuration)
    : BackgroundService, IMessageHandlerWorker
{
    private readonly Random _random = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting {workerAmount} Outbound message workers...", configuration.WorkersQuantity);

        var tasks = new List<Task>();

        for (var i = 0; i < configuration.WorkersQuantity; i++)
        {
            tasks.Add(Worker(Guid.NewGuid(), stoppingToken));
        }

        await Task.WhenAll(tasks.ToArray());
        
        logger.LogInformation("All Outbound message workers stopped");
    }

    private async Task Worker(Guid workerId, CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(10, 300)), stoppingToken);
        logger.LogInformation("Started Outbound message worker {WorkerId}", workerId);

        await using var scope = serviceProvider.CreateAsyncScope();
        var messageHandler = scope.ServiceProvider.GetRequiredService<IMessageHandler>();
        var dbContext = GetContext(scope);

        while (!stoppingToken.IsCancellationRequested)
        {
            
            await using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

            try
            {
                await messageHandler.HandleQueuedOutboundMessages(transaction);

                await dbContext.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }
            catch (Exception e)
            {
                await transaction.RollbackAsync(stoppingToken);
                logger.LogError(e, "Error processing outbound messages");
            }

            await Task.Delay(TimeSpan.FromSeconds(configuration.PoolingTime), stoppingToken);
        }
        
        logger.LogInformation("Stopped Outbound message worker {WorkerId}", workerId);
    }

    public abstract DbContext GetContext(AsyncServiceScope scope);
}