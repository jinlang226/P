// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PChecker.Runtime.Values;

namespace PChecker.Runtime.TraceValidation
{
    internal sealed class TraceRecord
    {
        internal readonly int Timestamp;
        internal readonly string EventType;
        internal readonly string PodName;
        internal readonly string TargetType;
        internal readonly Dictionary<string, string> DetailsString;
        internal readonly Dictionary<string, int> DetailsInt;
        internal readonly Dictionary<string, bool> DetailsBool;

        internal TraceRecord(int timestamp, string eventType, string podName, string targetType,
            Dictionary<string, string> detailsString,
            Dictionary<string, int> detailsInt,
            Dictionary<string, bool> detailsBool)
        {
            Timestamp = timestamp;
            EventType = eventType ?? string.Empty;
            PodName = podName ?? string.Empty;
            TargetType = targetType ?? string.Empty;
            DetailsString = detailsString ?? new Dictionary<string, string>();
            DetailsInt = detailsInt ?? new Dictionary<string, int>();
            DetailsBool = detailsBool ?? new Dictionary<string, bool>();
        }

        internal PNamedTuple ToPayload()
        {
            var details = new PMap();
            foreach (var kvp in DetailsString)
            {
                details[(PString)kvp.Key] = (PString)(kvp.Value ?? string.Empty);
            }

            var detailsInt = new PMap();
            foreach (var kvp in DetailsInt)
            {
                detailsInt[(PString)kvp.Key] = new PInt(kvp.Value);
            }

            var detailsBool = new PMap();
            foreach (var kvp in DetailsBool)
            {
                detailsBool[(PString)kvp.Key] = (PBool)kvp.Value;
            }

            return new PNamedTuple(
                new[] { "timestamp", "eventType", "podName", "details", "detailsInt", "detailsBool" },
                new IPValue[]
                {
                    new PInt(Timestamp),
                    (PString)EventType,
                    (PString)PodName,
                    details,
                    detailsInt,
                    detailsBool
                });
        }

        internal static List<TraceRecord> LoadTrace(string traceFile)
        {
            using var stream = File.OpenRead(traceFile);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("events", out var eventsElement) ||
                eventsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Trace file must contain an 'events' array.");
            }

            var traceEvents = new List<TraceRecord>();
            foreach (var ev in eventsElement.EnumerateArray())
            {
                var eventType = GetJsonString(ev, "msg") ?? GetJsonString(ev, "eventType");
                if (string.IsNullOrEmpty(eventType))
                {
                    continue;
                }

                var podName = GetJsonString(ev, "pod") ??
                              GetJsonString(ev, "name") ??
                              GetJsonString(ev, "cluster") ??
                              "unknown";

                var timestamp = ParseTimestamp(ev);
                var targetType = GetJsonString(ev, "targetType") ??
                                 GetJsonString(ev, "target") ??
                                 GetJsonString(ev, "receiverType") ??
                                 GetJsonString(ev, "machineType") ??
                                 string.Empty;
                var (detailsString, detailsInt, detailsBool) = NormalizeDetails(ev);
                traceEvents.Add(new TraceRecord(timestamp, eventType, podName, targetType, detailsString, detailsInt, detailsBool));
            }

            return traceEvents;
        }

        private static int ParseTimestamp(JsonElement ev)
        {
            if (ev.TryGetProperty("timestamp", out var ts))
            {
                if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var num))
                {
                    return (int)num;
                }

                if (ts.ValueKind == JsonValueKind.String)
                {
                    var str = ts.GetString();
                    if (!string.IsNullOrEmpty(str) && DateTimeOffset.TryParse(str, out var dto))
                    {
                        return (int)dto.ToUnixTimeSeconds();
                    }
                }
            }

            return 0;
        }

        private static (Dictionary<string, string>, Dictionary<string, int>, Dictionary<string, bool>) NormalizeDetails(JsonElement ev)
        {
            var detailsString = new Dictionary<string, string>();
            var detailsInt = new Dictionary<string, int>();
            var detailsBool = new Dictionary<string, bool>();

            if (ev.TryGetProperty("details", out var detailsElement) &&
                detailsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in detailsElement.EnumerateObject())
                {
                    AddJsonDetail(prop.Name, prop.Value, detailsString, detailsInt, detailsBool);
                }
            }

            var ns = GetJsonString(ev, "namespace");
            if (!string.IsNullOrEmpty(ns) && !detailsString.ContainsKey("namespace"))
            {
                detailsString["namespace"] = ns;
            }

            var name = GetJsonString(ev, "name");
            if (!string.IsNullOrEmpty(name) && !detailsString.ContainsKey("name"))
            {
                detailsString["name"] = name;
            }

            return (detailsString, detailsInt, detailsBool);
        }

        private static void AddJsonDetail(string key, JsonElement value,
            Dictionary<string, string> detailsString,
            Dictionary<string, int> detailsInt,
            Dictionary<string, bool> detailsBool)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                case JsonValueKind.False:
                {
                    var boolValue = value.GetBoolean();
                    detailsBool[key] = boolValue;
                    detailsString[key] = boolValue ? "true" : "false";
                    return;
                }
                case JsonValueKind.Number:
                {
                    if (value.TryGetInt32(out var intValue))
                    {
                        detailsInt[key] = intValue;
                        detailsString[key] = intValue.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (value.TryGetInt64(out var longValue))
                    {
                        detailsString[key] = longValue.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (value.TryGetDouble(out var doubleValue))
                    {
                        detailsString[key] = doubleValue.ToString(CultureInfo.InvariantCulture);
                    }
                    return;
                }
                case JsonValueKind.String:
                {
                    detailsString[key] = value.GetString() ?? string.Empty;
                    return;
                }
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                {
                    detailsString[key] = value.ToString();
                    return;
                }
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                {
                    detailsString[key] = "null";
                    return;
                }
            }
        }

        private static string GetJsonString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            return null;
        }
    }
}
