#!/bin/bash
set -e

echo "=========================================="
echo "  TSDuck Interop Library Test Suite"
echo "=========================================="
echo ""

FAILED=0
mkdir -p /tests/results

run_test() {
    local name=$1
    echo "Running: $name"
    if ./$name --gtest_output=xml:results/${name}.xml; then
        echo "  PASSED: $name"
    else
        echo "  FAILED: $name"
        FAILED=$((FAILED + 1))
    fi
    echo ""
}

echo "=== Unit Tests ==="
run_test test_seqlock
run_test test_pid_tracker
run_test test_pcr_analyzer
run_test test_iat_analyzer
run_test test_alignment_buffer
run_test test_failover_manager
run_test test_quality_switch_trigger
run_test test_duckcontext
run_test test_psi_monitor
run_test test_scte35_monitor
run_test test_nal_parser
run_test test_tr101290
run_test test_keyframe_aligner

echo "=== Integration Tests ==="
run_test test_analyzer_integration
run_test test_restamper_integration
run_test test_streamer_integration

echo "=== Stress Tests ==="
STRESS_DURATION_MS=500 run_test test_stress

echo "=== Network Simulation Tests ==="
run_test test_network_simulation

echo "=========================================="
if [ $FAILED -eq 0 ]; then
    echo "  All tests passed!"
    exit 0
else
    echo "  $FAILED test(s) failed"
    exit 1
fi
