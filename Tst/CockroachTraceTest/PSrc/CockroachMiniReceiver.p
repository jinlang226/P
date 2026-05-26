// Minimal trace receiver: parses CockroachDB trace events and announces
// model-level facts for the spec to observe.
machine CockroachMiniReceiver {
    var lastSpecNodes: int;

    start state Active {
        on eTraceEvent do (evt: tTraceEvent) {
            var newNodes: int;
            if (evt.eventType == "DecommissionCommandReturn") {
                if (GetBool(evt.detailsBool, "success", false)) {
                    announce eDecommissionSucceeded;
                }
            }
            if (evt.eventType == "CockroachOperatorSpecObserved") {
                if ("specNodes" in keys(evt.detailsInt)) {
                    newNodes = evt.detailsInt["specNodes"];
                    if (lastSpecNodes > 0 && newNodes < lastSpecNodes) {
                        announce eScaleDownRequested;
                    }
                    lastSpecNodes = newNodes;
                }
            }
        }
    }

    fun GetBool(m: map[string, bool], key: string, fallback: bool): bool {
        if (key in m) {
            return m[key];
        }
        return fallback;
    }
}
