﻿namespace Khala.EventSourcing
{
    using System.Collections.Generic;

    public interface IEventSourced : IVersionedEntity
    {
        IEnumerable<IDomainEvent> PendingEvents { get; }
    }
}