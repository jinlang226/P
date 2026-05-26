// Minimal tTraceEvent type — must match TraceRecord.cs ToPayload() field layout exactly.
type tTraceEvent = (
    timestamp: int,
    eventType: string,
    podName: string,
    details: map[string, string],
    detailsInt: map[string, int],
    detailsBool: map[string, bool]
);

event eTraceEvent: tTraceEvent;

// Model-level events announced to the spec monitor.
event eDecommissionSucceeded;
event eScaleDownRequested;
