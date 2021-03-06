﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Dasync.EETypes;
using Dasync.EETypes.Descriptors;
using Dasync.EETypes.Fabric;
using Dasync.EETypes.Intents;
using Dasync.Serialization;
using static Dasync.Fabric.InMemory.InMemoryDataStore;

namespace Dasync.Fabric.InMemory
{
    public class InMemoryFabricConnector : IFabricConnector, IFabricConnectorWithConfiguration
    {
        private readonly InMemoryDataStore _dataStore;
        private readonly ISerializer _serializer;
        private readonly string _serializerFormat;

        public InMemoryFabricConnector(
            InMemoryDataStore dataStore,
            ISerializer serializer,
            string serializerFormat)
        {
            _dataStore = dataStore;
            _serializer = serializer;
            _serializerFormat = serializerFormat;
        }

        public string ConnectorType => "InMemory";

        public object Configuration => new InMemoryFabricConnectorConfiguration
        {
            DataStoreId = _dataStore.Id,
            SerializerFormat = _serializerFormat
        };

        public Task<ActiveRoutineInfo> ScheduleRoutineAsync(
            ExecuteRoutineIntent intent, CancellationToken ct)
        {
#warning Need to send message first, then create routine record.

            var routineId = Interlocked.Increment(ref _dataStore.RoutineCounter);

            var routineRecord = new RoutineStateRecord
            {
                ETag = DateTime.UtcNow.Ticks.ToString("X16"),
                Id = routineId.ToString(),
                Completion = new TaskCompletionSource<string>(),
                Continuation = intent.Continuation == null ? null : _serializer.SerializeToString(intent.Continuation)
            };

            lock (_dataStore.Routines)
            {
                _dataStore.Routines.Add(routineRecord.Id, routineRecord);
            }

            var transitionDescriptor = new TransitionDescriptor
            {
                Type = TransitionType.InvokeRoutine,
                ETag = routineRecord.ETag
            };

            var routineDescriptor = new RoutineDescriptor
            {
                MethodId = intent.MethodId,
                IntentId = intent.Id,
                RoutineId = routineRecord.Id,
                ETag = routineRecord.ETag
            };

            var message = new Message
            {
                //["IntentId"] = _serializer.Serialize(intent.Id),
                [nameof(TransitionDescriptor)] = _serializer.SerializeToString(transitionDescriptor),
                [nameof(ServiceId)] = _serializer.SerializeToString(intent.ServiceId),
                [nameof(RoutineDescriptor)] = _serializer.SerializeToString(routineDescriptor),
                ["Parameters"] = _serializer.SerializeToString(intent.Parameters)
            };

            _dataStore.ScheduleMessage(message);

            var info = new ActiveRoutineInfo
            {
                RoutineId = routineRecord.Id
            };

            return Task.FromResult(info);
        }

        public async Task<ActiveRoutineInfo> PollRoutineResultAsync(
            ActiveRoutineInfo info, CancellationToken ct)
        {
            var routineRecord = _dataStore.GetRoutineRecord(info.RoutineId);
            var resultData = await routineRecord.Completion.Task;
            var result = _serializer.Deserialize<TaskResult>(resultData);
            return new ActiveRoutineInfo
            {
                RoutineId = routineRecord.Id,
                Result = result
            };
        }

        public Task<ActiveRoutineInfo> ScheduleContinuationAsync(
            ContinueRoutineIntent intent, CancellationToken ct)
        {
            var transitionDescriptor = new TransitionDescriptor
            {
                Type = TransitionType.ContinueRoutine,
                ETag = intent.Continuation.Routine.ETag
            };

            var message = new Message
            {
                //["IntentId"] = _serializer.Serialize(intent.Id),
                [nameof(TransitionDescriptor)] = _serializer.SerializeToString(transitionDescriptor),
                [nameof(ServiceId)] = _serializer.SerializeToString(intent.Continuation.ServiceId),
                [nameof(RoutineDescriptor)] = _serializer.SerializeToString(intent.Continuation.Routine),
                [nameof(RoutineResultDescriptor)] = _serializer.SerializeToString(intent.Result),
                DeliverAt = intent.Continuation.ContinueAt?.ToUniversalTime()
            };

            _dataStore.ScheduleMessage(message);

            var info = new ActiveRoutineInfo
            {
                RoutineId = intent.Continuation.Routine.RoutineId
            };

            return Task.FromResult(info);
        }
    }
}
