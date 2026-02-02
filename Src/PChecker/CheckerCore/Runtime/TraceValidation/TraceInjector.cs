// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PChecker.Runtime.Events;
using PChecker.Runtime.StateMachines;
using PChecker.SystematicTesting;

namespace PChecker.Runtime.TraceValidation
{
    internal sealed class TraceInjector
    {
        private readonly ControlledRuntime Runtime;
        private readonly List<TraceRecord> Trace;
        private readonly HashSet<string> TargetTypeNames;
        private readonly Dictionary<string, StateMachineId> Targets;
        private readonly object Gate = new object();
        private readonly System.IO.TextWriter Logger;
        private TraceValidator Validator;
        private bool Started;
        private int RetryCount;
        private bool StartCheckScheduled;
        private int StartCheckAttempts;
        private int LastTargetCount;
        private const int MaxStartCheckAttempts = 5;
        private const int MaxRetryCount = 10;
        private int TraceIndex;

        internal TraceInjector(ControlledRuntime runtime, string traceFile, IEnumerable<string> targetTypeNames,
            System.IO.TextWriter logger)
        {
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            Trace = TraceRecord.LoadTrace(traceFile);
            Logger = logger;
            TargetTypeNames = new HashSet<string>(StringComparer.Ordinal);
            Targets = new Dictionary<string, StateMachineId>(StringComparer.Ordinal);

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

            Logger?.WriteLine($"TraceInjector: loaded {Trace.Count} events.");
            if (TargetTypeNames.Count > 0)
            {
                Logger?.WriteLine($"TraceInjector: target filters = [{string.Join(", ", TargetTypeNames)}].");
            }
        }

        internal void OnStateMachineCreated(StateMachine stateMachine)
        {
            if (stateMachine is null)
            {
                return;
            }

            lock (Gate)
            {
                if (TargetTypeNames.Count == 0)
                {
                    var canReceiveTrace = stateMachine.receives.Any(r =>
                        string.Equals(r, "eTraceEvent", StringComparison.Ordinal) ||
                        r.EndsWith(".eTraceEvent", StringComparison.Ordinal));
                    if (canReceiveTrace && !IsLikelyDriver(stateMachine.Id.Type))
                    {
                        Targets[stateMachine.Id.Type] = stateMachine.Id;
                        Logger?.WriteLine($"TraceInjector: target added {stateMachine.Id.Type} ({stateMachine.Id}).");
                    }
                }
                else
                {
                if (TargetTypeNames.Count > 0)
                {
                    foreach (var target in TargetTypeNames)
                    {
                        if (Targets.ContainsKey(target))
                        {
                            continue;
                        }

                        if (MatchesType(target, stateMachine.Id.Type))
                        {
                            Targets[target] = stateMachine.Id;
                            Logger?.WriteLine($"TraceInjector: target added {stateMachine.Id.Type} ({stateMachine.Id}).");
                            break;
                        }
                    }
                }
                }

                TryStartInjectionLocked();
            }
        }

        internal void AttachValidator(TraceValidator validator)
        {
            Validator = validator;
        }

        private void TryStartInjectionLocked()
        {
            if (Started)
            {
                return;
            }

            if (TargetTypeNames.Count == 0)
            {
                if (Targets.Count > 0)
                {
                    ScheduleStartCheckLocked();
                }
                return;
            }

            if (Targets.Count == TargetTypeNames.Count)
            {
                Started = true;
                Logger?.WriteLine($"TraceInjector: starting with {Targets.Count} targets.");
                StartInjection();
            }
        }

        private void ScheduleStartCheckLocked()
        {
            if (StartCheckScheduled)
            {
                return;
            }

            StartCheckScheduled = true;
            Runtime.TaskController.ScheduleAction(CheckStartInjection, null, CancellationToken.None);
        }

        private void CheckStartInjection()
        {
            lock (Gate)
            {
                StartCheckScheduled = false;
                if (Started)
                {
                    return;
                }

                if (Targets.Count >= 2)
                {
                    Started = true;
                    Logger?.WriteLine($"TraceInjector: starting with {Targets.Count} targets.");
                    StartInjection();
                    return;
                }

                if (Targets.Count != LastTargetCount)
                {
                    LastTargetCount = Targets.Count;
                    StartCheckAttempts = 0;
                }

                StartCheckAttempts++;
                if (StartCheckAttempts >= MaxStartCheckAttempts && Targets.Count > 0)
                {
                    Started = true;
                    Logger?.WriteLine($"TraceInjector: starting with {Targets.Count} targets.");
                    StartInjection();
                    return;
                }

                if (Targets.Count > 0)
                {
                    ScheduleStartCheckLocked();
                }
            }
        }

        private void StartInjection()
        {
            Logger?.WriteLine("TraceInjector: starting injection.");
            // Targets are already resolved (especially when explicitly specified),
            // so inject immediately to avoid waiting on a scheduling point.
            InjectNextTraceEvent();
        }

        internal void OnTraceMatched()
        {
            if (!Started)
            {
                return;
            }

            if (TraceIndex >= Trace.Count)
            {
                return;
            }

            Runtime.TaskController.ScheduleAction(InjectNextTraceEvent, null, CancellationToken.None);
        }

        private void InjectNextTraceEvent()
        {
            if (Validator == null)
            {
                Runtime.Assert(false, "Trace injection failed: validator is not attached.");
                return;
            }

            var eventType = GetTraceEventType();
            if (eventType is null)
            {
                Runtime.Assert(false, "Trace injection failed: could not resolve eTraceEvent type.");
                return;
            }

            if (TraceIndex >= Trace.Count)
            {
                return;
            }

            var record = Trace[TraceIndex];
            var targetIds = ResolveTargets(record.TargetType);
            if (targetIds.Count == 0)
            {
                RetryCount++;
                if (RetryCount <= MaxRetryCount)
                {
                    Logger?.WriteLine($"TraceInjector: no targets available, retry {RetryCount}/{MaxRetryCount}.");
                    Runtime.TaskController.ScheduleAction(InjectNextTraceEvent, null, CancellationToken.None);
                    return;
                }

                Runtime.Assert(false, "Trace injection failed: no target state machines available.");
                return;
            }

            var payload = record.ToPayload();
            var ev = (Event)Activator.CreateInstance(eventType);
            ev.Payload = payload;

            foreach (var targetId in targetIds)
            {
                Runtime.SendEventFromRuntime(targetId, ev);
            }

            TraceIndex++;
        }

        private List<StateMachineId> ResolveTargets(string recordTargetType)
        {
            lock (Gate)
            {
                if (TargetTypeNames.Count > 0)
                {
                    return Targets.Values.ToList();
                }

                if (!string.IsNullOrWhiteSpace(recordTargetType))
                {
                    var matches = Targets.Where(kvp => MatchesType(recordTargetType, kvp.Key))
                        .Select(kvp => kvp.Value)
                        .ToList();
                    return matches;
                }

                return Targets.Values.ToList();
            }
        }

        private static bool MatchesType(string target, string fullName)
        {
            if (string.Equals(target, fullName, StringComparison.Ordinal))
            {
                return true;
            }

            return fullName != null && fullName.EndsWith("." + target, StringComparison.Ordinal);
        }

        private static bool IsLikelyDriver(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return false;
            }

            return fullName.EndsWith("Driver", StringComparison.Ordinal) ||
                   fullName.EndsWith("Test", StringComparison.Ordinal) ||
                   fullName.EndsWith("Tester", StringComparison.Ordinal);
        }

        private static Type GetTraceEventType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try
                {
                    type = assembly.GetTypes().FirstOrDefault(t => t.Name == "eTraceEvent");
                }
                catch
                {
                    continue;
                }

                if (type != null && typeof(Event).IsAssignableFrom(type))
                {
                    return type;
                }
            }

            return null;
        }
    }
}
