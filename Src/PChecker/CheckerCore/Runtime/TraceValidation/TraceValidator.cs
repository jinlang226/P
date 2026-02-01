// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PChecker.Runtime.Events;
using PChecker.Runtime.Values;

namespace PChecker.Runtime.TraceValidation
{
    internal sealed class TraceValidator
    {
        private readonly List<TraceRecord> Trace;
        private readonly HashSet<string> TargetTypeNames;
        private readonly Dictionary<string, TraceRecord> ExpectedByEventType;
        private int Index;

        internal int MatchedCount => Index;
        internal int TotalCount => Trace.Count;

        internal TraceValidator(string traceFile, IEnumerable<string> targetTypeNames, TextWriter logger)
        {
            if (string.IsNullOrEmpty(traceFile))
            {
                throw new ArgumentException("Trace file path cannot be empty.");
            }

            Trace = TraceRecord.LoadTrace(traceFile);
            TargetTypeNames = new HashSet<string>(StringComparer.Ordinal);
            ExpectedByEventType = new Dictionary<string, TraceRecord>(StringComparer.Ordinal);
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

        internal string GetExpectedTargetType()
        {
            if (Index < 0 || Index >= Trace.Count)
            {
                return null;
            }

            var targetType = Trace[Index].TargetType;
            return string.IsNullOrWhiteSpace(targetType) ? null : targetType;
        }

        internal bool TryMatch(string receiverType, Event e, out string errorMessage)
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
                return false;
            }

            var expected = Trace[Index];
            if (!string.IsNullOrWhiteSpace(expected.TargetType) &&
                !IsTargetTypeMatch(expected.TargetType, receiverType))
            {
                return true;
            }
            var mismatch = Compare(expected, actual, Index);
            if (mismatch != null)
            {
                errorMessage = mismatch;
                return false;
            }

            UpdateExpectedValues(expected);
            Index++;
            return true;
        }

        internal bool TryMatchValue(Event e, out string errorMessage)
        {
            errorMessage = null;
            if (!TryExtractTraceEvent(e, out var actual))
            {
                return true;
            }

            var key = BuildExpectedKey(actual.EventType, GetReconcileId(actual));
            if (!ExpectedByEventType.TryGetValue(key, out var expected))
            {
                errorMessage = $"Trace value validation failed: no expected values for eventType='{actual.EventType}'.";
                return false;
            }

            var mismatch = CompareValues(expected, actual);
            if (mismatch != null)
            {
                errorMessage = mismatch;
                return false;
            }

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
                return null;
            }

            var next = Trace[Index];
            return $"Trace validation failed: expected {Trace.Count} events, but only observed {Index}. " +
                   $"Next expected eventType='{next.EventType}'.";
        }

        private void UpdateExpectedValues(TraceRecord record)
        {
            var key = BuildExpectedKey(record.EventType, GetReconcileId(record));
            ExpectedByEventType[key] = record;
        }

        private static string BuildExpectedKey(string eventType, string reconcileId)
        {
            if (string.IsNullOrEmpty(reconcileId))
            {
                return eventType ?? string.Empty;
            }

            return $"{eventType}::{reconcileId}";
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

        private static bool TryExtractTraceEvent(Event e, out TraceRecord traceEvent)
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
