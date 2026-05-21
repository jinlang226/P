// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PChecker.Runtime.Events;
using PChecker.Runtime.Values;

namespace PChecker.Runtime.TraceValidation
{
    internal sealed class TraceValidator
    {
        private readonly List<TraceRecord> Trace;
        private readonly HashSet<string> TargetTypeNames;
        private readonly Dictionary<string, Queue<TraceRecord>> ExpectedByEventType;
        private readonly Dictionary<string, Queue<PendingValue>> PendingValues;
        private readonly Dictionary<string, int> LastSpecInt;
        private readonly Dictionary<string, bool> LastSpecBool;
        private readonly HashSet<string> SpecIntFields;
        private readonly HashSet<string> SpecBoolFields;
        private readonly string TraceFile;
        private readonly TraceValidationReport Report;
        private int Index;

        internal int MatchedCount => Index;
        internal int TotalCount => Trace.Count;

        internal TraceValidator(string traceFile, IEnumerable<string> targetTypeNames,
            IEnumerable<string> specIntFields, IEnumerable<string> specBoolFields,
            TextWriter logger)
        {
            if (string.IsNullOrEmpty(traceFile))
            {
                throw new ArgumentException("Trace file path cannot be empty.");
            }

            TraceFile = traceFile;
            Trace = TraceRecord.LoadTrace(traceFile);
            TargetTypeNames = new HashSet<string>(StringComparer.Ordinal);
            ExpectedByEventType = new Dictionary<string, Queue<TraceRecord>>(StringComparer.Ordinal);
            PendingValues = new Dictionary<string, Queue<PendingValue>>(StringComparer.Ordinal);
            LastSpecInt = new Dictionary<string, int>(StringComparer.Ordinal);
            LastSpecBool = new Dictionary<string, bool>(StringComparer.Ordinal);
            SpecIntFields = specIntFields != null
                ? new HashSet<string>(specIntFields, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            SpecBoolFields = specBoolFields != null
                ? new HashSet<string>(specBoolFields, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            Report = new TraceValidationReport(traceFile);
            InitializeSpecFromTrace();
            if (targetTypeNames != null)
            {
                foreach (var name in targetTypeNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        TargetTypeNames.Add(name.Trim());
                    }
                }
            }
            logger?.WriteLine($"TraceValidator: loaded {Trace.Count} events from {traceFile}");
        }

        internal bool IsCompleted => Index >= Trace.Count;

        private void InitializeSpecFromTrace()
        {
            foreach (var record in Trace)
            {
                if (string.Equals(record.EventType, "SpecObserved", StringComparison.Ordinal))
                {
                    continue;
                }
                UpdateLastSpecFromRecord(record);
                if (LastSpecInt.Count > 0 || LastSpecBool.Count > 0)
                {
                    break;
                }
            }
        }

        internal string GetExpectedTargetType()
        {
            if (Index < 0 || Index >= Trace.Count)
            {
                return null;
            }

            var targetType = Trace[Index].TargetType;
            return string.IsNullOrWhiteSpace(targetType) ? null : targetType;
        }

        internal bool TryGetExpectedRecord(out TraceRecord record)
        {
            if (Index < 0 || Index >= Trace.Count)
            {
                record = null;
                return false;
            }

            record = Trace[Index];
            return true;
        }

        internal bool TryMatch(string receiverType, Event e, out string errorMessage)
        {
            return TryMatch(receiverType, null, e, out errorMessage);
        }

        internal bool TryMatch(string receiverType, string receiverState, Event e, out string errorMessage)
        {
            errorMessage = null;
            if (TargetTypeNames.Count > 0 && !IsTargetType(receiverType))
            {
                return true;
            }
            if (!TryExtractTraceEvent(e, out var actual))
            {
                return true;
            }

            if (Index >= Trace.Count)
            {
                errorMessage = $"Trace validation failed: observed extra event '{actual.EventType}' after trace end.";
                RecordEventMatch(Index, receiverType, receiverState, null, actual, errorMessage);
                return false;
            }

            var expected = Trace[Index];
            if (!string.IsNullOrWhiteSpace(expected.TargetType) &&
                !IsTargetTypeMatch(expected.TargetType, receiverType))
            {
                return true;
            }
            var mismatch = Compare(expected, actual, Index);
            RecordEventMatch(Index, receiverType, receiverState, expected, actual, mismatch);
            if (mismatch != null)
            {
                errorMessage = mismatch;
                return false;
            }

            var valueMismatch = UpdateExpectedValues(expected);
            if (valueMismatch != null)
            {
                errorMessage = valueMismatch;
                return false;
            }
            Index++;
            return true;
        }

        internal bool TryMatchValue(Event e, out string errorMessage)
        {
            return TryMatchValue(null, null, e, out errorMessage);
        }

        internal bool TryMatchValue(string receiverType, string receiverState, Event e, out string errorMessage)
        {
            errorMessage = null;
            if (TargetTypeNames.Count > 0 && !IsTargetType(receiverType))
            {
                return true;
            }
            
            if (!TryExtractTraceEvent(e, out var actual))
            {
                return true;
            }

            var key = BuildExpectedKey(actual.EventType, GetReconcileId(actual), GetTraceId(actual));
            if (!ExpectedByEventType.TryGetValue(key, out var expectedQueue) || expectedQueue.Count == 0)
            {
                var pendingQueue = GetOrCreatePendingQueue(key);
                pendingQueue.Enqueue(new PendingValue(actual, receiverType, receiverState));
                return true;
            }

            var expected = expectedQueue.Peek();
            var mismatch = CompareValues(expected, actual);
            if (mismatch != null)
            {
                errorMessage = mismatch;
                return false;
            }

            expectedQueue.Dequeue();
            RecordValueMatch(receiverType, receiverState, expected, actual, string.Empty);

            return true;
        }

        private bool IsTargetType(string receiverType)
        {
            if (string.IsNullOrEmpty(receiverType))
            {
                return false;
            }

            if (TargetTypeNames.Contains(receiverType))
            {
                return true;
            }

            foreach (var target in TargetTypeNames)
            {
                if (receiverType.EndsWith("." + target, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTargetTypeMatch(string targetType, string receiverType)
        {
            if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(receiverType))
            {
                return false;
            }

            if (string.Equals(targetType, receiverType, StringComparison.Ordinal))
            {
                return true;
            }

            return receiverType.EndsWith("." + targetType, StringComparison.Ordinal);
        }

        internal string GetUnmatchedError()
        {
            if (Index >= Trace.Count)
            {
                foreach (var pendingQueue in PendingValues.Values)
                {
                    if (pendingQueue.Count == 0)
                    {
                        continue;
                    }

                    var pending = pendingQueue.Peek();
                    return $"Trace value validation failed: observed value event '{pending.Actual.EventType}' without a matching trace entry.";
                }

                foreach (var expectedQueue in ExpectedByEventType.Values)
                {
                    if (expectedQueue.Count == 0)
                    {
                        continue;
                    }

                    var expected = expectedQueue.Peek();
                    return $"Trace value validation failed: expected value event '{expected.EventType}' was never observed.";
                }

                return null;
            }

            var next = Trace[Index];
            return $"Trace validation failed: expected {Trace.Count} events, but only observed {Index}. " +
                   $"Next expected eventType='{next.EventType}'.";
        }

        private string UpdateExpectedValues(TraceRecord record)
        {
            var key = BuildExpectedKey(record.EventType, GetReconcileId(record), GetTraceId(record));
            var mismatch = EnqueueExpectedOrMatchPending(key, record);
            if (mismatch != null)
            {
                return mismatch;
            }

            return UpdateExpectedSpecSnapshot(record);
        }

        private string UpdateExpectedSpecSnapshot(TraceRecord record)
        {
            // Tyler trace-driven models emit SpecSnapshotBefore/After when handling
            // SpecObserved semantics, not for every dequeued trace event. Keep the
            // running spec cache updated on all events, but only enqueue snapshot
            // expectations at SpecObserved boundaries.
            if (!string.Equals(record.EventType, "SpecObserved", StringComparison.Ordinal))
            {
                UpdateLastSpecFromRecord(record);
                return null;
            }

            var reconcileId = GetReconcileId(record);
            var traceId = GetTraceId(record);
            if (LastSpecInt.Count > 0 || LastSpecBool.Count > 0)
            {
                var beforeDetails = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(reconcileId))
                {
                    beforeDetails["reconcileId"] = reconcileId;
                }
                if (!string.IsNullOrWhiteSpace(traceId))
                {
                    beforeDetails["traceId"] = traceId;
                }

                var beforeRecord = new TraceRecord(
                    record.Timestamp,
                    "SpecSnapshotBefore",
                    "unknown",
                    string.Empty,
                    beforeDetails,
                    new Dictionary<string, int>(LastSpecInt, StringComparer.Ordinal),
                    new Dictionary<string, bool>(LastSpecBool, StringComparer.Ordinal));

                var beforeMismatch = EnqueueExpectedOrMatchPending(
                    BuildExpectedKey(beforeRecord.EventType, reconcileId, traceId), beforeRecord);
                if (beforeMismatch != null)
                {
                    return beforeMismatch;
                }
            }

            UpdateLastSpecFromRecord(record);

            if (LastSpecInt.Count == 0 && LastSpecBool.Count == 0)
            {
                return null;
            }

            var afterDetails = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(reconcileId))
            {
                afterDetails["reconcileId"] = reconcileId;
            }
            if (!string.IsNullOrWhiteSpace(traceId))
            {
                afterDetails["traceId"] = traceId;
            }

            var afterRecord = new TraceRecord(
                record.Timestamp,
                "SpecSnapshotAfter",
                "unknown",
                string.Empty,
                afterDetails,
                new Dictionary<string, int>(LastSpecInt, StringComparer.Ordinal),
                new Dictionary<string, bool>(LastSpecBool, StringComparer.Ordinal));

            return EnqueueExpectedOrMatchPending(
                BuildExpectedKey(afterRecord.EventType, reconcileId, traceId), afterRecord);
        }

        private void EnqueueExpected(string key, TraceRecord record)
        {
            if (!ExpectedByEventType.TryGetValue(key, out var queue))
            {
                queue = new Queue<TraceRecord>();
                ExpectedByEventType[key] = queue;
            }
            queue.Enqueue(record);
        }

        private string EnqueueExpectedOrMatchPending(string key, TraceRecord expected)
        {
            if (PendingValues.TryGetValue(key, out var pendingQueue) && pendingQueue.Count > 0)
            {
                var pending = pendingQueue.Dequeue();
                if (pendingQueue.Count == 0)
                {
                    PendingValues.Remove(key);
                }

                var mismatch = CompareValues(expected, pending.Actual);
                RecordValueMatch(pending.ReceiverType, pending.ReceiverState, expected, pending.Actual, mismatch);
                return mismatch;
            }

            EnqueueExpected(key, expected);
            return null;
        }

        private Queue<PendingValue> GetOrCreatePendingQueue(string key)
        {
            if (!PendingValues.TryGetValue(key, out var queue))
            {
                queue = new Queue<PendingValue>();
                PendingValues[key] = queue;
            }

            return queue;
        }

        private void UpdateLastSpecFromRecord(TraceRecord record)
        {
            foreach (var field in SpecIntFields)
            {
                if (record.DetailsInt != null && record.DetailsInt.TryGetValue(field, out var v))
                    LastSpecInt[field] = v;
                if (record.DetailsString != null &&
                    record.DetailsString.TryGetValue(field, out var s) &&
                    int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    LastSpecInt[field] = parsed;
            }
            foreach (var field in SpecBoolFields)
            {
                if (record.DetailsBool != null && record.DetailsBool.TryGetValue(field, out var v))
                    LastSpecBool[field] = v;
                if (record.DetailsString != null &&
                    record.DetailsString.TryGetValue(field, out var s) &&
                    bool.TryParse(s, out var parsed))
                    LastSpecBool[field] = parsed;
            }
        }

        private static string BuildExpectedKey(string eventType, string reconcileId, string traceId)
        {
            if (string.IsNullOrEmpty(reconcileId) && string.IsNullOrEmpty(traceId))
            {
                return eventType ?? string.Empty;
            }

            return $"{eventType}::{reconcileId ?? string.Empty}::{traceId ?? string.Empty}";
        }

        private static string GetReconcileId(TraceRecord record)
        {
            if (record.DetailsString != null &&
                record.DetailsString.TryGetValue("reconcileId", out var rid) &&
                !string.IsNullOrWhiteSpace(rid))
            {
                return rid;
            }

            return string.Empty;
        }

        private static string GetTraceId(TraceRecord record)
        {
            if (record.DetailsString != null &&
                record.DetailsString.TryGetValue("traceId", out var traceId) &&
                !string.IsNullOrWhiteSpace(traceId))
            {
                return traceId;
            }

            return string.Empty;
        }

        private static string CompareValues(TraceRecord expected, TraceRecord actual)
        {
            if (!string.Equals(expected.EventType, actual.EventType, StringComparison.Ordinal))
            {
                return $"Trace value validation mismatch: expected eventType='{expected.EventType}', actual eventType='{actual.EventType}'.";
            }

            if (!string.IsNullOrEmpty(actual.PodName) &&
                !string.Equals(actual.PodName, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(expected.PodName, actual.PodName, StringComparison.Ordinal))
                {
                    return $"Trace value validation mismatch: expected podName='{expected.PodName}', actual podName='{actual.PodName}'.";
                }
            }

            var mismatch = CompareValueMap(actual.DetailsString, expected.DetailsString, "details");
            if (mismatch != null)
            {
                return mismatch;
            }

            mismatch = CompareValueMap(actual.DetailsInt, expected.DetailsInt, "detailsInt");
            if (mismatch != null)
            {
                return mismatch;
            }

            mismatch = CompareValueMap(actual.DetailsBool, expected.DetailsBool, "detailsBool");
            if (mismatch != null)
            {
                return mismatch;
            }

            return null;
        }

        private static string CompareValueMap<T>(Dictionary<string, T> actual, Dictionary<string, T> expected, string label)
        {
            if (actual == null || actual.Count == 0)
            {
                return null;
            }

            expected ??= new Dictionary<string, T>();
            foreach (var kvp in actual)
            {
                if (!expected.TryGetValue(kvp.Key, out var expectedValue))
                {
                    return $"Trace value validation mismatch: missing {label} key '{kvp.Key}'.";
                }

                if (!EqualityComparer<T>.Default.Equals(kvp.Value, expectedValue))
                {
                    return $"Trace value validation mismatch: {label}['{kvp.Key}'] expected '{expectedValue}', actual '{kvp.Value}'.";
                }
            }

            return null;
        }

        private static string Compare(TraceRecord expected, TraceRecord actual, int index)
        {
            if (!string.Equals(expected.EventType, actual.EventType, StringComparison.Ordinal))
            {
                return $"Trace validation mismatch at index {index}: expected eventType='{expected.EventType}', " +
                       $"actual eventType='{actual.EventType}'.";
            }

            if (!string.IsNullOrEmpty(expected.PodName) &&
                !string.Equals(expected.PodName, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(expected.PodName, actual.PodName, StringComparison.Ordinal))
                {
                    return $"Trace validation mismatch at index {index}: expected podName='{expected.PodName}', " +
                           $"actual podName='{actual.PodName ?? "null"}'.";
                }
            }

            var detailsMismatch = CompareMap(expected.DetailsString, actual.DetailsString, "details", index);
            if (detailsMismatch != null)
            {
                return detailsMismatch;
            }

            detailsMismatch = CompareMap(expected.DetailsInt, actual.DetailsInt, "detailsInt", index);
            if (detailsMismatch != null)
            {
                return detailsMismatch;
            }

            detailsMismatch = CompareMap(expected.DetailsBool, actual.DetailsBool, "detailsBool", index);
            if (detailsMismatch != null)
            {
                return detailsMismatch;
            }

            return null;
        }

        private static string CompareMap<T>(Dictionary<string, T> expected, Dictionary<string, T> actual, string label, int index)
        {
            if (expected == null || expected.Count == 0)
            {
                return null;
            }

            actual ??= new Dictionary<string, T>();
            foreach (var kvp in expected)
            {
                if (!actual.TryGetValue(kvp.Key, out var actualValue))
                {
                    return $"Trace validation mismatch at index {index}: missing {label} key '{kvp.Key}'.";
                }

                if (!EqualityComparer<T>.Default.Equals(kvp.Value, actualValue))
                {
                    return $"Trace validation mismatch at index {index}: {label}['{kvp.Key}'] " +
                           $"expected '{kvp.Value}', actual '{actualValue}'.";
                }
            }

            return null;
        }

        internal static bool TryExtractTraceEvent(Event e, out TraceRecord traceEvent)
        {
            traceEvent = default;
            if (e is null || e.Payload is null)
            {
                return false;
            }

            if (e.Payload is not PNamedTuple namedTuple)
            {
                return false;
            }

            if (!TryGetNamedTupleField(namedTuple, "eventType", out var eventTypeValue))
            {
                return false;
            }

            var eventType = GetStringValue(eventTypeValue);
            if (string.IsNullOrEmpty(eventType))
            {
                return false;
            }

            var podName = string.Empty;
            if (TryGetNamedTupleField(namedTuple, "podName", out var podValue))
            {
                podName = GetStringValue(podValue) ?? string.Empty;
            }

            var details = new Dictionary<string, string>();
            var detailsInt = new Dictionary<string, int>();
            var detailsBool = new Dictionary<string, bool>();

            if (TryGetNamedTupleField(namedTuple, "details", out var detailsValue) && detailsValue is PMap pDetails)
            {
                details = ConvertStringMap(pDetails);
            }

            if (TryGetNamedTupleField(namedTuple, "detailsInt", out var detailsIntValue) &&
                detailsIntValue is PMap pDetailsInt)
            {
                detailsInt = ConvertIntMap(pDetailsInt);
            }

            if (TryGetNamedTupleField(namedTuple, "detailsBool", out var detailsBoolValue) &&
                detailsBoolValue is PMap pDetailsBool)
            {
                detailsBool = ConvertBoolMap(pDetailsBool);
            }

            traceEvent = new TraceRecord(0, eventType, podName, string.Empty, details, detailsInt, detailsBool);
            return true;
        }

        private static bool TryGetNamedTupleField(PNamedTuple tuple, string fieldName, out IPValue value)
        {
            var idx = tuple.fieldNames.IndexOf(fieldName);
            if (idx < 0)
            {
                value = null;
                return false;
            }

            value = tuple.fieldValues[idx];
            return true;
        }

        private sealed class PendingValue
        {
            internal PendingValue(TraceRecord actual, string receiverType, string receiverState)
            {
                Actual = actual;
                ReceiverType = receiverType;
                ReceiverState = receiverState;
            }

            internal TraceRecord Actual { get; }
            internal string ReceiverType { get; }
            internal string ReceiverState { get; }
        }

        private static string GetStringValue(IPValue value)
        {
            return value switch
            {
                PString pString => pString,
                PInt pInt => ((int)pInt).ToString(CultureInfo.InvariantCulture),
                PBool pBool => ((bool)pBool) ? "true" : "false",
                _ => value?.ToString()
            };
        }

        private static Dictionary<string, string> ConvertStringMap(PMap map)
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in map)
            {
                var key = GetStringValue(kvp.Key);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                result[key] = GetStringValue(kvp.Value) ?? string.Empty;
            }

            return result;
        }

        private static Dictionary<string, int> ConvertIntMap(PMap map)
        {
            var result = new Dictionary<string, int>();
            foreach (var kvp in map)
            {
                var key = GetStringValue(kvp.Key);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (kvp.Value is PInt pInt)
                {
                    result[key] = (int)pInt;
                }
            }

            return result;
        }

        internal void WriteReport(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return;
            }

            try
            {
                Report.TotalCount = TotalCount;
                Report.MatchedCount = MatchedCount;
                var path = Path.Combine(outputDirectory, "trace_validation_report.json");
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(Report, options);
                File.WriteAllText(path, json);
            }
            catch
            {
                // Do not fail trace validation if report generation fails.
            }
        }

        private void RecordEventMatch(int index, string receiverType, string receiverState, TraceRecord expected, TraceRecord actual, string mismatch)
        {
            if (actual == null)
            {
                return;
            }

            Report.EventMatches.Add(new TraceEventMatch
            {
                Index = index,
                ReceiverType = receiverType ?? string.Empty,
                ReceiverState = receiverState ?? string.Empty,
                Expected = TraceRecordDto.From(expected),
                Actual = TraceRecordDto.From(actual),
                Result = string.IsNullOrEmpty(mismatch) ? "match" : "mismatch",
                Error = mismatch ?? string.Empty
            });
        }

        private void RecordValueMatch(string receiverType, string receiverState, TraceRecord expected, TraceRecord actual, string mismatch, string resultOverride = null)
        {
            if (actual == null)
            {
                return;
            }

            Report.ValueMatches.Add(new TraceValueMatch
            {
                ReceiverType = receiverType ?? string.Empty,
                ReceiverState = receiverState ?? string.Empty,
                Expected = TraceRecordDto.From(expected),
                Actual = TraceRecordDto.From(actual),
                Result = resultOverride ?? (string.IsNullOrEmpty(mismatch) ? "match" : "mismatch"),
                Error = mismatch ?? string.Empty
            });
        }

        internal sealed class TraceValidationReport
        {
            public TraceValidationReport(string traceFile)
            {
                TraceFile = traceFile;
            }

            public string TraceFile { get; set; }
            public int TotalCount { get; set; }
            public int MatchedCount { get; set; }
            public List<TraceEventMatch> EventMatches { get; } = new();
            public List<TraceValueMatch> ValueMatches { get; } = new();
        }

        internal sealed class TraceEventMatch
        {
            public int Index { get; set; }
            public string ReceiverType { get; set; }
            public string ReceiverState { get; set; }
            public TraceRecordDto Expected { get; set; }
            public TraceRecordDto Actual { get; set; }
            public string Result { get; set; }
            public string Error { get; set; }
        }

        internal sealed class TraceValueMatch
        {
            public string ReceiverType { get; set; }
            public string ReceiverState { get; set; }
            public TraceRecordDto Expected { get; set; }
            public TraceRecordDto Actual { get; set; }
            public string Result { get; set; }
            public string Error { get; set; }
        }

        internal sealed class TraceRecordDto
        {
            public string EventType { get; set; }
            public string PodName { get; set; }
            public string ReconcileId { get; set; }
            public Dictionary<string, string> Details { get; set; }
            public Dictionary<string, int> DetailsInt { get; set; }
            public Dictionary<string, bool> DetailsBool { get; set; }

            public static TraceRecordDto From(TraceRecord record)
            {
                if (record == null)
                {
                    return null;
                }

                return new TraceRecordDto
                {
                    EventType = record.EventType,
                    PodName = record.PodName,
                    ReconcileId = GetReconcileId(record),
                    Details = record.DetailsString == null ? new Dictionary<string, string>() : new Dictionary<string, string>(record.DetailsString),
                    DetailsInt = record.DetailsInt == null ? new Dictionary<string, int>() : new Dictionary<string, int>(record.DetailsInt),
                    DetailsBool = record.DetailsBool == null ? new Dictionary<string, bool>() : new Dictionary<string, bool>(record.DetailsBool)
                };
            }
        }
        
        private static Dictionary<string, bool> ConvertBoolMap(PMap map)
        {
            var result = new Dictionary<string, bool>();
            foreach (var kvp in map)
            {
                var key = GetStringValue(kvp.Key);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (kvp.Value is PBool pBool)
                {
                    result[key] = (bool)pBool;
                }
            }

            return result;
        }

    }
}
