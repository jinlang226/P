// Fan-out tests: no spec monitor; validation is purely via trace matching.

test FanoutBothTest [main = FanoutBothDriver]:
    {ReceiverA, ReceiverB, FanoutBothDriver};

test FanoutAdapterTest [main = FanoutAdapterDriver]:
    {TraceAdapter, ReceiverA, FanoutAdapterDriver};

// Value-event tests: no spec monitor; bugs come from TryMatchValue failures.

test ValueEchoTest [main = ValueEchoDriver]:
    {ValueEchoer, ValueEchoDriver};

test WrongBoolTest [main = WrongBoolDriver]:
    {WrongBoolEmitter, WrongBoolDriver};

test ExtraFieldTest [main = ExtraFieldDriver]:
    {ExtraFieldEmitter, ExtraFieldDriver};

// --- Fan-out drivers ---

machine FanoutBothDriver {
    start state Init {
        entry {
            new ReceiverA();
            new ReceiverB();
            send this, eKickstart;
        }
        on eKickstart goto Active;
    }
    state Active {
        ignore eTraceEvent;
    }
}

machine FanoutAdapterDriver {
    start state Init {
        entry {
            new TraceAdapter();
            new ReceiverA();
            send this, eKickstart;
        }
        on eKickstart goto Active;
    }
    state Active {
        ignore eTraceEvent;
    }
}

// --- Value-event drivers ---

machine ValueEchoDriver {
    start state Init {
        entry {
            new ValueEchoer();
            send this, eKickstart;
        }
        on eKickstart goto Active;
    }
    state Active {
        ignore eTraceEvent;
        ignore eValueEvent;
    }
}

machine WrongBoolDriver {
    start state Init {
        entry {
            new WrongBoolEmitter();
            send this, eKickstart;
        }
        on eKickstart goto Active;
    }
    state Active {
        ignore eTraceEvent;
        ignore eValueEvent;
    }
}

machine ExtraFieldDriver {
    start state Init {
        entry {
            new ExtraFieldEmitter();
            send this, eKickstart;
        }
        on eKickstart goto Active;
    }
    state Active {
        ignore eTraceEvent;
        ignore eValueEvent;
    }
}
