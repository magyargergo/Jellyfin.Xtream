# Fuzz Testing

This directory contains fuzz testing harnesses for the TsDuck interop library.
Fuzzing helps discover edge cases, memory corruption, and undefined behavior
that unit tests might miss.

## Prerequisites

### libFuzzer (recommended)
libFuzzer is included with Clang. Install LLVM/Clang:

```bash
# Ubuntu/Debian
sudo apt install clang llvm

# Arch
sudo pacman -S clang llvm

# macOS
xcode-select --install
```

### AFL++ (alternative)
```bash
# Ubuntu/Debian
sudo apt install afl++

# Or build from source
git clone https://github.com/AFLplusplus/AFLplusplus
cd AFLplusplus && make && sudo make install
```

## Building Fuzz Harnesses

### With libFuzzer

```bash
cd native/tsduck_interop

# Alignment buffer fuzzer
clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
    -I src -I include \
    tests/fuzz/fuzz_alignment_buffer.cpp \
    -o fuzz_alignment_buffer

# PSI monitor fuzzer (requires TsDuck)
clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
    -I src -I include \
    $(pkg-config --cflags --libs tsduck) \
    tests/fuzz/fuzz_psi_monitor.cpp \
    -o fuzz_psi_monitor

# PID tracker fuzzer
clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
    -I src -I include \
    $(pkg-config --cflags --libs tsduck) \
    tests/fuzz/fuzz_pid_tracker.cpp \
    -o fuzz_pid_tracker
```

### With AFL++

```bash
# Build with AFL++ instrumentation
afl-clang++ -g -O1 -DFUZZ_MAIN \
    -I src -I include \
    tests/fuzz/fuzz_alignment_buffer.cpp \
    -o fuzz_alignment_buffer_afl
```

## Running Fuzzers

### libFuzzer

```bash
# Create corpus directory
mkdir -p corpus/alignment
mkdir -p corpus/psi
mkdir -p corpus/pid

# Seed corpus with valid TS data
# (Add valid .ts file snippets to corpus directories)

# Run alignment buffer fuzzer
./fuzz_alignment_buffer corpus/alignment/ -max_len=65536

# Run PSI monitor fuzzer
./fuzz_psi_monitor corpus/psi/ -max_len=8192

# Run PID tracker fuzzer
./fuzz_pid_tracker corpus/pid/ -max_len=16384
```

### AFL++

```bash
# Create input/output directories
mkdir -p in/alignment out/alignment

# Seed with valid data
echo -ne '\x47\x00\x00\x10...' > in/alignment/seed.ts

# Run AFL++
afl-fuzz -i in/alignment -o out/alignment ./fuzz_alignment_buffer_afl @@
```

## Useful libFuzzer Options

- `-max_len=N` — Maximum input size in bytes
- `-jobs=N` — Run N fuzzing jobs in parallel
- `-workers=N` — Number of worker threads per job
- `-timeout=N` — Timeout in seconds per test case
- `-dict=file` — Use dictionary for smart mutations
- `-rss_limit_mb=N` — Memory limit per test case
- `-print_final_stats=1` — Print statistics at exit

## Creating Seed Corpus

Good seed inputs help the fuzzer find interesting code paths faster.

### Alignment Buffer
- Valid TS stream fragments (188-byte multiples)
- Partial packets
- Streams with sync byte issues

### PSI Monitor
- Valid PAT sections in TS packets
- Valid PMT sections in TS packets
- Multi-section PAT/PMT
- Corrupted sections (CRC errors)

### PID Tracker
- Sequences of valid PID records
- Rapid PID changes
- Reset sequences

## Interpreting Results

### Crash Reports
When a crash is found, libFuzzer saves the input to `crash-<hash>`.
Reproduce with:
```bash
./fuzz_alignment_buffer crash-abc123
```

### Coverage
Enable coverage to find under-tested code:
```bash
clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
    -fprofile-instr-generate -fcoverage-mapping \
    ...
./fuzz_alignment_buffer corpus/ -max_len=65536 -runs=100000
llvm-profdata merge -sparse default.profraw -o default.profdata
llvm-cov show ./fuzz_alignment_buffer -instr-profile=default.profdata
```

## CI Integration

Add to CI pipeline:
```yaml
fuzz_tests:
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - name: Install dependencies
      run: sudo apt install clang llvm libtsduck-dev
    - name: Build fuzzers
      run: |
        clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
          -I src -I include \
          tests/fuzz/fuzz_alignment_buffer.cpp \
          -o fuzz_alignment_buffer
    - name: Run fuzzer (short)
      run: ./fuzz_alignment_buffer corpus/ -max_len=65536 -max_total_time=60
```

## Security Notes

- Always run fuzzers with sanitizers (ASan, UBSan)
- Use memory limits to prevent OOM from crashing the host
- Run in isolated environments (containers, VMs) when possible
- Review and triage all crashes promptly
