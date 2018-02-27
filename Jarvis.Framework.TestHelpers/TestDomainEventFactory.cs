using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fasterflect;
using Jarvis.Framework.Shared.Events;
using Jarvis.Framework.Shared.Messages;
using Jarvis.Framework.Shared.IdentitySupport;

namespace Jarvis.Framework.TestHelpers
{
    public static class DomainEventTestExtensions
    {
        public static T AssignIdForTest<T>(this T evt, IIdentity id) where T : DomainEvent
        {
            evt.SetPropertyValue("AggregateId", id);
            return evt;
        }

        public static T AssignPositionValues<T>(
            this T evt,
            Int64 checkpointToken,
            Int64 aggregateVersion,
            Int32 eventPosition) where T : DomainEvent
        {
            evt.SetPropertyValue("CheckpointToken", checkpointToken);
            evt.SetPropertyValue("EventPosition", eventPosition);
            evt.SetPropertyValue("Version", aggregateVersion);

            return evt;
        }

        public static T AssignIssuedByForTest<T>(this T evt, String issuedBy) where T : DomainEvent
        {
            if (evt.Context == null)
                evt.SetPropertyValue("Context", new Dictionary<String, Object>());

            evt.Context.Add(MessagesConstants.UserId, issuedBy);
            return evt;
        }
    }
}
