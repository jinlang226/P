// Minimal receivers used to verify multi-target fan-out behaviour.
// ReceiverA and ReceiverB both accept eTraceEvent; neither is named "TraceAdapter"
// so the injector fans out to both when both appear in --traceinject-target.

machine ReceiverA {
    start state Active {
        on eTraceEvent do (evt: tTraceEvent) { }
    }
}

machine ReceiverB {
    start state Active {
        on eTraceEvent do (evt: tTraceEvent) { }
    }
}

// Named "TraceAdapter": ResolveTargets() gives this machine exclusive priority when
// it appears alongside other targets in --traceinject-target.
machine TraceAdapter {
    start state Active {
        on eTraceEvent do (evt: tTraceEvent) { }
    }
}
