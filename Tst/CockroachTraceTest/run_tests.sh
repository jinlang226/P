#!/bin/bash
set -euo pipefail

PCHECKER="$(cd "$(dirname "$0")" && pwd)/../../Bld/Drops/Release/Binaries/net8.0/p"
PROJDIR="$(cd "$(dirname "$0")" && pwd)"
DLL="$PROJDIR/PGenerated/PChecker/net8.0/CockroachTraceTest.dll"
TRACES="$PROJDIR/traces"

pass=0
fail=0

run_test() {
    local name="$1"
    local trace="$2"
    local expect_bugs="$3"
    local outdir="$PROJDIR/PCheckerOut_${name}"

    echo "[$name] expect bugs=$expect_bugs"
    "$PCHECKER" check "$DLL" \
        -tc TraceInjectEntryTest \
        --tracevalidate "$trace" \
        --traceinject-target CockroachMiniReceiver \
        --tracevalidate-target CockroachMiniReceiver \
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

run_test "valid_scaleup"   "$TRACES/valid_scaleup.json"   "0"
run_test "valid_scaledown" "$TRACES/valid_scaledown.json" "0"
run_test "invalid_scaledown" "$TRACES/invalid_scaledown.json" "1"

run_test "failed_decommission_then_scaledown"            "$TRACES/failed_decommission_then_scaledown.json"            "1"
run_test "two_scaledowns_one_decommission"               "$TRACES/two_scaledowns_one_decommission.json"               "1"
run_test "scaledown_scaleup_scaledown_no_new_decommission" "$TRACES/scaledown_scaleup_scaledown_no_new_decommission.json" "1"
run_test "multiple_failures_then_success_then_scaledown" "$TRACES/multiple_failures_then_success_then_scaledown.json" "0"
run_test "many_reconciles_scaleup_only"                  "$TRACES/many_reconciles_scaleup_only.json"                  "0"

echo ""
echo "=== Results: $pass passed, $fail failed ==="
[ $fail -eq 0 ]
