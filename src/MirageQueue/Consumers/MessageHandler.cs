﻿using MassTransit;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MirageQueue.Consumers.Abstractions;
using MirageQueue.Messages.Entities;
using MirageQueue.Messages.Repositories;

namespace MirageQueue.Consumers;

public class MessageHandler(
    ILogger<MessageHandler> logger,
    IOutboundMessageRepository outboundMessageRepository,
    IInboundMessageRepository inboundMessageRepository,
    Dispatcher dispatcher,
    MirageQueueConfiguration configuration)
    : IMessageHandler
{
    private async Task<List<InboundMessage>> GetInboundMessages(IDbContextTransaction dbTransaction)
    {
        return await inboundMessageRepository.GetQueuedMessages(configuration.AckMessageQuantity, dbTransaction);
    }

    private async Task<List<OutboundMessage>> GetOutboundMessage(IDbContextTransaction dbTransaction)
    {
        return await outboundMessageRepository.GetQueuedMessages(configuration.AckMessageQuantity, dbTransaction);
    }

    public async Task HandleQueuedOutboundMessages(IDbContextTransaction dbTransaction)
    {
        var messages = await GetOutboundMessage(dbTransaction);
        await outboundMessageRepository.SetTransaction(dbTransaction);
        var tasks = messages.Select(CallOutboundDispatcher).ToList();
        
        await Task.WhenAll(tasks);
    }

    public async Task CallOutboundDispatcher(OutboundMessage message)
    {
        try
        {
            await dispatcher.ProcessOutboundMessage(message);
            message.ChangeStatus(OutboundMessageStatus.Processing);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while processing outbound message {MessageId}", message.Id);
            message.ChangeStatus(OutboundMessageStatus.Failed);
        }
    }

    public async Task HandleQueuedInboundMessages(IDbContextTransaction dbTransaction)
    {
        var inboundMessages = await GetInboundMessages(dbTransaction);

        try
        {
            foreach (var inboundMessage in inboundMessages)
            {
                await CovertInboundToOutboundMessage(inboundMessage);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while processing inbound messages");
        }
    }

    private async Task CovertInboundToOutboundMessage(InboundMessage inboundMessage)
    {
        await CreateOutboundMessages(inboundMessage);
        inboundMessage.Status = InboundMessageStatus.Queued;
        inboundMessage.UpdateAt = DateTime.UtcNow;
        await inboundMessageRepository.Update(inboundMessage);
        await inboundMessageRepository.SaveChanges();
    }

    private async Task CreateOutboundMessages(InboundMessage inboundMessage)
    {
        var consumers = dispatcher.Consumers.Where(x => x.MessageContract == inboundMessage.MessageContract);

        foreach (var consumer in consumers)
        {
            if (await outboundMessageRepository.Any(x => x.ConsumerEndpoint == consumer.ConsumerEndpoint
                                                                && x.InboundMessageId == inboundMessage.Id)) continue;
            var outboundMessage = new OutboundMessage
            {
                Id = NewId.NextSequentialGuid(),
                ConsumerEndpoint = consumer.ConsumerEndpoint,
                MessageContract = consumer.MessageContract,
                Content = inboundMessage.Content,
                CreateAt = DateTime.UtcNow,
                Status = OutboundMessageStatus.New,
                InboundMessageId = inboundMessage.Id
            };

            await outboundMessageRepository.InsertAsync(outboundMessage);
        }

        inboundMessage.Status = InboundMessageStatus.Queued;
        inboundMessage.UpdateAt = DateTime.UtcNow;
        await inboundMessageRepository.Update(inboundMessage);
    }
}