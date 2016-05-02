﻿using Castle.Core.Logging;
using Jarvis.Framework.Shared.Domain;
using Jarvis.Framework.Shared.Domain.Serialization;
using Jarvis.Framework.Shared.Events;
using Jarvis.Framework.Shared.IdentitySupport.Serialization;
using Jarvis.NEventStoreEx.CommonDomainEx;
using MongoDB.Bson.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Jarvis.Framework.Shared.IdentitySupport
{
    public static class MongoFlatMapper
    {
        private static Boolean _enabled = false;
        private static ILogger _logger = NullLogger.Instance;

        public static void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        public static void EnableFlatMapping(
            Boolean enableForAllId = true)
        {
            if (_enabled) return;

            BsonClassMap.RegisterClassMap<DomainEvent>(map =>
            {
                map.AutoMap();
                map
                    .MapProperty(x => x.AggregateId)
                    .SetSerializer(new EventStoreIdentityBsonSerializer());

            });

            if (enableForAllId)
            {
                AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;
                AddSerializerForAllStringBasedIdFromThisApplication();
            }

            _enabled = true;
        }

        private static void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            AddSerializerForAllStringBasedIdFromAssembly(args.LoadedAssembly);
        }

        public static void AddSerializerForAllStringBasedIdFromAssembly(Assembly assembly)
        {
            var serializerGeneric = typeof(TypedEventStoreIdentityBsonSerializer<>);
            var asmName = assembly.FullName;
            _logger.InfoFormat("Scanning assembly {0} for Mongo flat mapping", asmName);
            foreach (var type in assembly.GetTypes()
                .Where(t => typeof(IIdentity).IsAssignableFrom(t) && !t.IsAbstract))
            {
                var fullName = type.FullName;
                _logger.DebugFormat("Registered IIdentity type {0}", type.FullName);
                var serializerType = serializerGeneric.MakeGenericType(type);
                BsonSerializer.RegisterSerializer(type, (IBsonSerializer) Activator.CreateInstance(serializerType));
                EventStoreIdentityCustomBsonTypeMapper.Register(type);
            }

            foreach (var type in assembly.GetTypes()
              .Where(t => typeof(StringValue).IsAssignableFrom(t) && !t.IsAbstract))
            {
                _logger.DebugFormat("Registered LowercaseStringValue type {0}", type.FullName);
                BsonSerializer.RegisterSerializer(type, new StringValueBsonSerializer(type));
                StringValueCustomBsonTypeMapper.Register(type);
            }
        }

        public static void AddSerializerForAllStringBasedIdFromThisApplication()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                AddSerializerForAllStringBasedIdFromAssembly(assembly);
            }
        }
    }
}
