// Machines that exercise the eValueEvent path (TryMatchValue).
// After handling each injected eTraceEvent the machine sends eValueEvent to itself;
// the framework intercepts the dequeue and calls TraceValidator.TryMatchValue.

machine ValueEchoer {
    start state Active {
        on eTraceEvent do (evt: tTraceEvent) {
            send this, eValueEvent, evt;
        }
        on eValueEvent do (evt: tTraceEvent) { }
    }
}

// Sends success=false regardless of what the trace says (trace has success=true).
// TryMatchValue detects the mismatch: detailsBool["success"] actual=false vs expected=true.
machine WrongBoolEmitter {
    start state Active {
        on eTraceEvent do (evt: tTraceEvent) {
            var wrongBool: map[string, bool];
            var payload: tTraceEvent;
            wrongBool["success"] = false;
            payload = evt;
            payload.detailsBool = wrongBool;
            send this, eValueEvent, payload;
        }
        on eValueEvent do (evt: tTraceEvent) { }
    }
}

// Sends detailsBool={"extra": true}; "extra" is not in the expected record so
// TryMatchValue fails: actual has a key the expected record never declared.
machine ExtraFieldEmitter {
    start state Active {
        on eTraceEvent do (evt: tTraceEvent) {
            var extraBool: map[string, bool];
            var payload: tTraceEvent;
            extraBool["extra"] = true;
            payload = evt;
            payload.detailsBool = extraBool;
            send this, eValueEvent, payload;
        }
        on eValueEvent do (evt: tTraceEvent) { }
    }
}
