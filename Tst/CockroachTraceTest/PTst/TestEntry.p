test TraceInjectEntryTest [main = TraceInjectEntryDriver]:
    assert NoScaleDownBeforeDecommission in
    {CockroachMiniReceiver, TraceInjectEntryDriver};

// Sent to the driver after all target machines are created so that the next
// NotifyDequeuedEvent call triggers TryStartInjection() with a full Targets map.
event eKickstart;

machine TraceInjectEntryDriver {
    start state Init {
        entry {
            new CockroachMiniReceiver();
            send this, eKickstart;
        }
        on eKickstart goto Active;
    }

    state Active {
        ignore eTraceEvent;
    }
}
