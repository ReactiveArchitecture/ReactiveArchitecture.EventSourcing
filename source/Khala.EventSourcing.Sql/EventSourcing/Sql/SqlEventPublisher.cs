﻿namespace Khala.EventSourcing.Sql
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Data.Entity;
    using System.Data.Entity.Core;
    using System.Data.Entity.Infrastructure;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Messaging;

    public class SqlEventPublisher : ISqlEventPublisher
    {
        private readonly Func<EventStoreDbContext> _dbContextFactory;
        private readonly IMessageSerializer _serializer;
        private readonly IMessageBus _messageBus;

        public SqlEventPublisher(
            Func<EventStoreDbContext> dbContextFactory,
            IMessageSerializer serializer,
            IMessageBus messageBus)
        {
            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public Task PublishPendingEvents(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            if (sourceId == Guid.Empty)
            {
                throw new ArgumentException(
                    $"{nameof(sourceId)} cannot be empty.", nameof(sourceId));
            }

            async Task Run()
            {
                using (EventStoreDbContext context = _dbContextFactory.Invoke())
                {
                    List<PendingEvent> pendingEvents = await LoadEvents(context, sourceId, cancellationToken).ConfigureAwait(false);
                    if (pendingEvents.Any() == false)
                    {
                        return;
                    }

                    await SendEvents(pendingEvents, cancellationToken).ConfigureAwait(false);
                    await RemoveEvents(context, pendingEvents, cancellationToken).ConfigureAwait(false);
                }
            }

            return Run();
        }

        private static Task<List<PendingEvent>> LoadEvents(
            EventStoreDbContext context,
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            IQueryable<PendingEvent> query = from e in context.PendingEvents
                                             where e.AggregateId == sourceId
                                             orderby e.Version
                                             select e;

            return query.ToListAsync(cancellationToken);
        }

        private Task SendEvents(List<PendingEvent> pendingEvents, CancellationToken cancellationToken)
            => _messageBus.SendBatch(RestoreEnvelopes(pendingEvents), cancellationToken);

        private List<Envelope> RestoreEnvelopes(List<PendingEvent> pendingEvents)
        {
            return pendingEvents.Select(e => RestoreEnvelope(e)).ToList();
        }

        private Envelope RestoreEnvelope(PendingEvent pendingEvent) =>
            new Envelope(
                pendingEvent.MessageId,
                pendingEvent.CorrelationId,
                _serializer.Deserialize(pendingEvent.EventJson));

        private static async Task RemoveEvents(
            EventStoreDbContext context,
            List<PendingEvent> pendingEvents,
            CancellationToken cancellationToken)
        {
            foreach (PendingEvent pendingEvent in pendingEvents)
            {
                try
                {
                    context.PendingEvents.Remove(pendingEvent);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException exception)
                when (exception.InnerException is OptimisticConcurrencyException)
                {
                    context.Entry(pendingEvent).State = EntityState.Detached;
                }
            }
        }

        public async void EnqueueAll(CancellationToken cancellationToken)
            => await PublishAllPendingEvents(cancellationToken).ConfigureAwait(false);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public async Task PublishAllPendingEvents(CancellationToken cancellationToken)
        {
            using (EventStoreDbContext context = _dbContextFactory.Invoke())
            {
                Loop:

                IEnumerable<Guid> source = await context
                    .PendingEvents
                    .OrderBy(e => e.AggregateId)
                    .Select(e => e.AggregateId)
                    .Take(1000)
                    .Distinct()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                Task[] tasks = source.Select(sourceId => PublishPendingEvents(sourceId, cancellationToken)).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);

                if (source.Any())
                {
                    goto Loop;
                }
            }
        }
    }
}
