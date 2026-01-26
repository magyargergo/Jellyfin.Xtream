#!/bin/bash
set -e

FUZZ_TIME=${FUZZ_TIME:-30}
echo "=========================================="
echo "  TSDuck Interop Fuzz Tests"
echo "  Time limit: ${FUZZ_TIME}s per fuzzer"
echo "=========================================="
echo ""

FAILED=0

run_fuzzer() {
    local name=$1
    local corpus=$2
    local max_len=$3
    echo "Running: $name (${FUZZ_TIME}s)"
    ./$name $corpus -max_len=$max_len -max_total_time=$FUZZ_TIME -print_final_stats=1
    local exit_code=$?
    if [ $exit_code -eq 0 ]; then
        echo "  PASSED: $name (exit code: $exit_code)"
    else
        echo "  FAILED: $name (exit code: $exit_code)"
        FAILED=$((FAILED + 1))
    fi
    echo ""
}

run_fuzzer fuzz_alignment_buffer corpus/alignment 65536
run_fuzzer fuzz_pid_tracker corpus/pid 16384
run_fuzzer fuzz_psi_monitor corpus/psi 8192
run_fuzzer fuzz_pcr_analyzer corpus/pcr 4096
run_fuzzer fuzz_tr101290 corpus/tr101290 16384
run_fuzzer fuzz_restamper corpus/restamper 65536
run_fuzzer fuzz_ring_buffer corpus/ring 8192
run_fuzzer fuzz_failover_manager corpus/failover 4096
run_fuzzer fuzz_keyframe_aligner corpus/keyframe 65536

echo "=========================================="
if [ $FAILED -eq 0 ]; then
    echo "  All fuzz tests passed!"
    exit 0
else
    echo "  $FAILED fuzz test(s) failed"
    exit 1
fi
