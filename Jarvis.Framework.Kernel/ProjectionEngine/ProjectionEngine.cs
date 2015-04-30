﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Castle.Core.Logging;
using Jarvis.Framework.Kernel.Events;
using Jarvis.Framework.Kernel.MultitenantSupport;
using Jarvis.Framework.Kernel.ProjectionEngine.Client;
using Jarvis.Framework.Shared.Events;
using Jarvis.Framework.Shared.Logging;
using Jarvis.Framework.Shared.MultitenantSupport;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using MongoDB.Driver.Linq;
using NEventStore;
using NEventStore.Client;
using NEventStore.Serialization;

namespace Jarvis.Framework.Kernel.ProjectionEngine
{
    public class ProjectionEngine
    {
        readonly Func<CommitPollingClient> _pollingClientFactory;

        private CommitPollingClient _client;

        readonly IConcurrentCheckpointTracker _checkpointTracker;

        readonly IHousekeeper _housekeeper;
        readonly INotifyCommitHandled _notifyCommitHandled;
        
        private readonly ProjectionMetrics _metrics;
        private long _maxDispatchedCheckpoint = 0;

        public IExtendedLogger Logger
        {
            get { return _logger; }
            set { _logger = value; }
        }
        IExtendedLogger _logger = NullLogger.Instance;

        public ILoggerFactory LoggerFactory
        {
            get { return _loggerFactory; }
            set { _loggerFactory = value; }
        }
        private ILoggerFactory _loggerFactory;

        IStoreEvents _eventstore;
        readonly ProjectionEngineConfig _config;
        readonly Dictionary<string, IProjection[]> _projectionsBySlot;
        private readonly IProjection[] _allProjections;

        private readonly IRebuildContext _rebuildContext;

        public ProjectionEngine(
            Func<CommitPollingClient> pollingClientFactory,
            IConcurrentCheckpointTracker checkpointTracker,
            IProjection[] projections,
            IHousekeeper housekeeper,
            IRebuildContext rebuildContext,
            INotifyCommitHandled notifyCommitHandled,
            ProjectionEngineConfig config)
        {
            _pollingClientFactory = pollingClientFactory;
            _checkpointTracker = checkpointTracker;
            _housekeeper = housekeeper;
            _rebuildContext = rebuildContext;
            _notifyCommitHandled = notifyCommitHandled;
            _config = config;

            if (_config.Slots[0] != "*")
            {
                projections = projections
                    .Where(x => _config.Slots.Any(y => y == x.GetSlotName()))
                    .ToArray();
            }

            _allProjections = projections;
            _projectionsBySlot = projections
                .GroupBy(x => x.GetSlotName())
                .ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.Priority).ToArray());

            _metrics = new ProjectionMetrics(_allProjections);
        }

        private void DumpProjections()
        {
            if (Logger.IsDebugEnabled)
            {
                foreach (var prj in _allProjections)
                {
                    if (Logger.IsDebugEnabled) Logger.DebugFormat("Projection: {0}", prj.GetType().FullName);
                }

                if (_allProjections.Length == 0)
                {
                    Logger.Warn("no projections found!");
                }
            }
        }

        public void Start()
        {
            if (Logger.IsDebugEnabled) Logger.DebugFormat("Starting projection engine on tenant {0}", _config.TenantId);

            int seconds = _config.ForcedGcSecondsInterval;

            StartPolling();
            if (Logger.IsDebugEnabled) Logger.Debug("Projection engine started");
        }

        public void Poll()
        {
            _client.Poll();
        }

        public void Stop()
        {
            if (Logger.IsDebugEnabled) Logger.Debug("Stopping projection engine");
            StopPolling();
            if (Logger.IsDebugEnabled) Logger.Debug("Projection engine stopped");
        }

        public void StartWithManualPoll()
        {
            if (_config.DelayedStartInMilliseconds == 0)
            {
                InnerStartWithManualPoll();
            }
            else
            {
                Task.Factory.StartNew(() =>
                {
                    Thread.Sleep(_config.DelayedStartInMilliseconds);
                    InnerStartWithManualPoll();
                });
            }
        }

        private void InnerStartWithManualPoll()
        {
            Init();
            _client.StartManualPolling(GetStartGlobalCheckpoint());
            Poll();
        }

        void StartPolling()
        {
            Init();
            _client.StartAutomaticPolling(GetStartGlobalCheckpoint(), _config.PollingMsInterval);
        }

        private void Init()
        {
            _maxDispatchedCheckpoint = 0;
            DumpProjections();

            TenantContext.Enter(_config.TenantId);

            _housekeeper.Init();

            _eventstore = Wireup
                .Init()
                .LogTo(t => new NEventStoreLog4NetLogger(LoggerFactory.Create(t)))
                .UsingMongoPersistence(() => _config.EventStoreConnectionString, new DocumentObjectSerializer())
                .InitializeStorageEngine()
                .Build();

            ConfigureProjections();

            // cleanup
            _housekeeper.RemoveAll(_eventstore.Advanced);

            var allSlots = _projectionsBySlot.Keys.ToArray();

            _client = _pollingClientFactory();

            foreach (var slotName in allSlots)
            {
                var startCheckpoint = GetStartCheckpointForSlot(slotName);
                Logger.InfoFormat("Slot {0} starts from {1}", slotName, startCheckpoint);

                var name = slotName;
                _client.AddConsumer(commit => DispatchCommit(commit, name, LongCheckpoint.Parse(startCheckpoint)));
            }
        }

        string GetStartGlobalCheckpoint()
        {
            var checkpoints = _projectionsBySlot.Keys.Select(GetStartCheckpointForSlot).Distinct().ToArray();
            var min = checkpoints.Any() ? checkpoints.Min(x => LongCheckpoint.Parse(x).LongValue) : 0;

            return new LongCheckpoint(min).Value;
        }

        string GetStartCheckpointForSlot(string slotName)
        {
            LongCheckpoint min = null;

            var projections = _projectionsBySlot[slotName];
            foreach (var projection in projections)
            {
                if (_checkpointTracker.NeedsRebuild(projection))
                    return null;

                var currentValue = _checkpointTracker.GetCurrent(projection);
                if (currentValue == null)
                    return null;

                var currentCheckpoint = LongCheckpoint.Parse(currentValue);

                if (min == null || currentCheckpoint.LongValue < min.LongValue)
                {
                    min = currentCheckpoint;
                }
            }

            return min.Value;
        }

        void ConfigureProjections()
        {
            _checkpointTracker.SetUp(_allProjections, 1);
            var lastCommit = GetLastCommitId();
            foreach (var slot in _projectionsBySlot)
            {
                foreach (var projection in slot.Value)
                {
                    if (_checkpointTracker.NeedsRebuild(projection))
                    {
                        projection.Drop();
                        projection.StartRebuild(_rebuildContext);
                        _checkpointTracker.RebuildStarted(projection, lastCommit);
                    }

                    projection.SetUp();
                }
            }
        }

        private String GetLastCommitId()
        {
            var collection = GetMongoCommitsCollection();
            var lastCommit = collection.Find(Query.GT("_id", BsonValue.Create(0)))
                .SetSortOrder(SortBy.Descending("_id"))
                .SetLimit(1)
                .FirstOrDefault();

            if (lastCommit == null) return null;

            return lastCommit["_id"].ToString();
        }

        void HandleError(Exception exception)
        {
            Logger.Fatal("Polling stopped", exception);
        }

        void StopPolling()
        {
            _client.StopPolling();
            _eventstore.Dispose();
        }

        void DispatchCommit(ICommit commit, string slotName, LongCheckpoint startCheckpoint)
        {
            Logger.ThreadProperties["commit"] = commit.CommitId;

            if (Logger.IsDebugEnabled) Logger.DebugFormat("Dispatching checkpoit {0} on tenant {1}", commit.CheckpointToken, _config.TenantId);
            TenantContext.Enter(_config.TenantId);
            var chkpoint = LongCheckpoint.Parse(commit.CheckpointToken);
            if (chkpoint.LongValue <= startCheckpoint.LongValue)
            {
                // già fatta
                return;
            }

            var projections = _projectionsBySlot[slotName];

            bool dispatchCommit = false;
            var eventMessages = commit.Events.ToArray();

            var projectionToUpdate = new List<string>();

            for (int index = 0; index < eventMessages.Length; index++)
            {
                var eventMessage = eventMessages[index];
                try
                {
                    var evt = (DomainEvent)eventMessage.Body;
                    Logger.ThreadProperties["evType"] = evt.GetType().Name;
                    Logger.ThreadProperties["evMsId"] = evt.MessageId;
                    string eventName = evt.GetType().Name;

                    foreach (var projection in projections)
                    {
                        var cname = projection.GetCommonName();
                        Logger.ThreadProperties["prj"] = cname;
                        var checkpointStatus = _checkpointTracker.GetCheckpointStatus(cname, commit.CheckpointToken);

                        bool handled;
                        long ticks = 0;
                        try
                        {
                            var sw = new Stopwatch();
                            sw.Start();
                            //if (Logger.IsDebugEnabled)Logger.DebugFormat("Handling {0} with {1}", commit.CheckpointToken, projection.GetType().Name);
                            handled = projection.Handle(evt, checkpointStatus.IsRebuilding);
                            //if (Logger.IsDebugEnabled)Logger.DebugFormat("Handled {0} with {1}", commit.CheckpointToken, projection.GetType().Name);
                            sw.Stop();
                            ticks = sw.ElapsedTicks;
                        }
                        catch (Exception ex)
                        {
                            Logger.FatalFormat(ex, "[{3}] Failed checkpoint {0}: {1} > {2}",
                                commit.CheckpointToken,
                                commit.StreamId,
                                eventName,
                                slotName
                            );
                            throw;
                        }

                        if (handled)
                        {
                            _metrics.Inc(cname, eventName, ticks);

                            Logger.DebugFormat("[{3}] [{4}] Handled checkpoint {0}: {1} > {2}",
                                commit.CheckpointToken,
                                commit.StreamId,
                                eventName,
                                slotName,
                                cname
                            );
                        }

                        if (!checkpointStatus.IsRebuilding)
                        {
                            if (handled)
                                dispatchCommit = true;

                            projectionToUpdate.Add(cname);
                        }
                        else
                        {
                            if (checkpointStatus.IsLast && (index == eventMessages.Length - 1))
                            {
                                projection.StopRebuild();
                                var meter = _metrics.GetMeter(cname);
                                _checkpointTracker.RebuildEnded(projection, meter);

                                Logger.InfoFormat("Rebuild done {0} @ {1}",
                                    projection.GetType().FullName,
                                    commit.CheckpointToken
                                );
                            }
                        }
                        Logger.ThreadProperties["prj"] = null;
                    }

                    Logger.ThreadProperties["evType"] = null;
                    Logger.ThreadProperties["evMsId"] = null;
                }
                catch (Exception ex)
                {
                    Logger.ErrorFormat(ex, "{0} -> {1}", eventMessage.Body, ex.Message);
                    throw;
                }
            }

            _checkpointTracker.UpdateSlot(slotName, commit.CheckpointToken);

            foreach (var cname in projectionToUpdate)
            {
                _checkpointTracker.SetCheckpoint(cname, commit.CheckpointToken);
            }

            if (dispatchCommit)
                _notifyCommitHandled.SetDispatched(commit);

            // ok in multithread wihout locks!
            if (_maxDispatchedCheckpoint < chkpoint.LongValue)
            {
                if (Logger.IsDebugEnabled)
                {
                    Logger.DebugFormat("Updating last dispatched checkpoint from {0} to {1}",
                        _maxDispatchedCheckpoint,
                        chkpoint.LongValue
                    );
                }
                _maxDispatchedCheckpoint = chkpoint.LongValue;
            }

            Logger.ThreadProperties["commit"] = null;
        }

        public void Update()
        {
            if (Logger.IsDebugEnabled) Logger.DebugFormat("Update triggered on tenant {0}", _config.TenantId);
            Poll();
        }

        public async Task UpdateAndWait()
        {
            if (Logger.IsDebugEnabled) Logger.DebugFormat("Polling from {0}", _config.EventStoreConnectionString);
            var collection = GetMongoCommitsCollection();
            int retryCount = 0;
            long lastDispatchedCheckpoint = 0;

            while (true)
            {
                var last = collection.FindAll().SetSortOrder(SortBy.Descending("_id")).SetLimit(1).SetFields("_id").FirstOrDefault();
                if (last != null)
                {
                    var checkpointId = last["_id"].AsInt64;
                    if (Logger.IsDebugEnabled) Logger.DebugFormat("Last checkpoint is {0}", checkpointId);

                    Poll();
                    await Task.Delay(1000);

                    var dispatched = _maxDispatchedCheckpoint;
                    if (Logger.IsDebugEnabled)
                    {
                        Logger.DebugFormat(
                            "Checkpoint {0} : Max dispatched {1}",
                            checkpointId, dispatched
                            );
                    }
                    if (checkpointId == dispatched)
                    {
                        last =
                            collection.FindAll()
                                .SetSortOrder(SortBy.Descending("_id"))
                                .SetLimit(1)
                                .SetFields("_id")
                                .FirstOrDefault();
                        checkpointId = last["_id"].AsInt64;
                        if (checkpointId == dispatched)
                        {
                            if (Logger.IsDebugEnabled) Logger.Debug("Polling done!");
                            return;
                        }
                    }
                    else
                    {
                        if (dispatched > lastDispatchedCheckpoint)
                        {
                            lastDispatchedCheckpoint = dispatched;
                            retryCount = 0;
                        }
                        else
                        {
                            if (retryCount++ > 10)
                            {
                                throw new Exception("Polling failed, engine freezed at checkpoint " + dispatched);
                            }
                        }
                    }
                }
                else
                {
                    if (Logger.IsDebugEnabled) Logger.Debug("Event store is empty");
                    return;
                }
            }
        }

        private MongoCollection<BsonDocument> GetMongoCommitsCollection()
        {
            var url = new MongoUrl(_config.EventStoreConnectionString);
            var client = new MongoClient(url);
            var db = client.GetServer().GetDatabase(url.DatabaseName);
            var collection = db.GetCollection("Commits");
            return collection;
        }
    }
}