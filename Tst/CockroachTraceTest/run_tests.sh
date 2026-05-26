#!/bin/bash
set -euo pipefail

PCHECKER="$(cd "$(dirname "$0")" && pwd)/../../Bld/Drops/Release/Binaries/net8.0/p"
PROJDIR="$(cd "$(dirname "$0")" && pwd)"
DLL="$PROJDIR/PGenerated/PChecker/net8.0/CockroachTraceTest.dll"
TRACES="$PROJDIR/traces"

pass=0
fail=0

# run_test NAME TRACE EXPECT_BUGS [TC [INJECT_TARGETS [VALIDATE_TARGET]]]
# INJECT_TARGETS is space-separated; each becomes a separate --traceinject-target flag.
run_test() {
    local name="$1"
    local trace="$2"
    local expect_bugs="$3"
    local tc="${4:-TraceInjectEntryTest}"
    local inject_target="${5:-CockroachMiniReceiver}"
    local validate_target="${6:-CockroachMiniReceiver}"
    local outdir="$PROJDIR/PCheckerOut_${name}"

    echo "[$name] expect bugs=$expect_bugs"

    local inject_args=()
    for t in $inject_target; do
        inject_args+=(--traceinject-target "$t")
    done

    "$PCHECKER" check "$DLL" \
        -tc "$tc" \
        --tracevalidate "$trace" \
        "${inject_args[@]}" \
        --tracevalidate-target "$validate_target" \
        --traceguided --seed 1 --timeout 20 \
        --outdir "$outdir" > /dev/null 2>&1 || true

    local summary="$outdir/BugFinding/CockroachTraceTest_pchecker_summary.txt"
    local bugs
    bugs=$(grep '^bugs:' "$summary" 2>/dev/null | cut -d: -f2 | tr -d ' ')
    if [ "$bugs" = "$expect_bugs" ]; then
        echo "  PASS (bugs=$bugs)"
        pass=$((pass+1))
    else
        echo "  FAIL (expected bugs=$expect_bugs, got bugs=$bugs)"
        fail=$((fail+1))
    fi
}

echo "=== CockroachDB P Runtime Tests ==="
echo ""
echo "Compiling..."
cd "$PROJDIR"
"$PCHECKER" compile > /dev/null 2>&1
echo "Done."
echo ""

# --- Original 8 tests ---
run_test "valid_scaleup"   "$TRACES/valid_scaleup.json"   "0"
run_test "valid_scaledown" "$TRACES/valid_scaledown.json" "0"
run_test "invalid_scaledown" "$TRACES/invalid_scaledown.json" "1"

run_test "failed_decommission_then_scaledown"            "$TRACES/failed_decommission_then_scaledown.json"            "1"
run_test "two_scaledowns_one_decommission"               "$TRACES/two_scaledowns_one_decommission.json"               "1"
run_test "scaledown_scaleup_scaledown_no_new_decommission" "$TRACES/scaledown_scaleup_scaledown_no_new_decommission.json" "1"
run_test "multiple_failures_then_success_then_scaledown" "$TRACES/multiple_failures_then_success_then_scaledown.json" "0"
run_test "many_reconciles_scaleup_only"                  "$TRACES/many_reconciles_scaleup_only.json"                  "0"

# --- Fan-out tests ---
# Tests 1-2: both ReceiverA and ReceiverB get every event (no TraceAdapter present).
run_test "fanout_both_recv_a" "$TRACES/fanout_two_targets.json" "0" "FanoutBothTest" "ReceiverA ReceiverB" "ReceiverA"
run_test "fanout_both_recv_b" "$TRACES/fanout_two_targets.json" "0" "FanoutBothTest" "ReceiverA ReceiverB" "ReceiverB"

# Tests 3-4: TraceAdapter takes exclusive priority; ReceiverA never receives events.
run_test "fanout_adapter_takes_all"    "$TRACES/fanout_two_targets.json" "0" "FanoutAdapterTest" "TraceAdapter ReceiverA" "TraceAdapter"
run_test "fanout_adapter_excludes_recv" "$TRACES/fanout_two_targets.json" "1" "FanoutAdapterTest" "TraceAdapter ReceiverA" "ReceiverA"

# --- Value-event tests ---
# Tests 5, 7: ValueEchoer echoes the exact payload; TryMatchValue should pass.
run_test "value_echo_correct"     "$TRACES/value_decomm_return.json"    "0" "ValueEchoTest"  "ValueEchoer"       "ValueEchoer"
run_test "value_wrong_bool"       "$TRACES/value_decomm_return.json"    "1" "WrongBoolTest"  "WrongBoolEmitter"  "WrongBoolEmitter"
run_test "value_multiple_correct" "$TRACES/value_multiple_returns.json" "0" "ValueEchoTest"  "ValueEchoer"       "ValueEchoer"
run_test "value_extra_field"      "$TRACES/value_decomm_return.json"    "1" "ExtraFieldTest" "ExtraFieldEmitter" "ExtraFieldEmitter"

echo ""
echo "=== Results: $pass passed, $fail failed ==="
[ $fail -eq 0 ]
