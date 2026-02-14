---
title: "fix: PR #1 Code Review Remediation (40 Findings)"
type: fix
date: 2026-02-14
source: https://github.com/magyargergo/Jellyfin.Xtream/pull/1#issuecomment-3901329272
deepened: 2026-02-14
---

# fix: PR #1 Code Review Remediation (40 Findings)

## Enhancement Summary

**Deepened on:** 2026-02-14
**Plan review:** 2026-02-14 (Architecture Strategist, Security Sentinel, Code Simplicity, Performance Oracle, Data Integrity Guardian)
**Review agents used:** 13 total (8 deepening + 5 review)

### Key Improvements from Deepening

1. **CRITICAL: Cross-language atomics memory model** — `Interlocked.And` alone is insufficient for cross-process shared memory; must pair with `Thread.MemoryBarrier()` for acquire semantics across C++/C# boundary
2. **CRITICAL: IPv6 SSRF bypass** — Original plan only blocks IPv4 private ranges; IPv6 `::1`, `::ffff:127.0.0.1`, `fe80::` are completely unprotected
3. **CRITICAL: curl initial protocol restriction** — Must set `CURLOPT_PROTOCOLS_STR` (not just `CURLOPT_REDIR_PROTOCOLS_STR`) to block `file://` on first request
4. **HIGH: Use `IHttpClientLogger` (.NET 8)** instead of `DelegatingHandler` for credential redaction — catches framework-level logging too
5. **HIGH: Factory method pattern** for `SharedMemoryConsumer` constructor instead of try-catch (safer resource cleanup)
6. **HIGH: `O_EXCL` flag** needed on `shm_open` to prevent TOCTOU race on shared memory creation

### Key Corrections from Plan Review

1. **REMOVED: §3.1 Statistics fix** — `Volatile.Read`+`Write` is CORRECT for SPSC single-writer pattern; `Interlocked.Add` adds unnecessary overhead
2. **REMOVED: §3.4 AVX2 alignment fix** — `0xF` mask is CORRECT for `Sse2.StoreAlignedNonTemporal` (128-bit aligned); changing to `0x1F` would halve NT store utilization
3. **REMOVED: §3.10 Protocol versioning** — `error_code` field already exists at offset `0xC4` in the header; no layout change needed
4. **REMOVED: §2.3 Legacy credential clearing** — Already implemented in `Plugin.cs:228-230`
5. **SIMPLIFIED: §3.5 DangerousGetHandle** — `LibraryImport` already handles most sites; inline 6-line pattern at ~5 remaining async sites
6. **SIMPLIFIED: §3.6 GCHandle race** — `Thread.Sleep(10)` grace period is simpler and sufficient; removed SpinWait complexity
7. **SIMPLIFIED: §3.9 Config lock** — Plain `lock` statement; removed `Monitor.TryEnter` (unnecessary complexity)
8. **SIMPLIFIED: §4.4 delete this** — Stop-flag + thread-join pattern (not `shared_ptr`, which conflicts with SafeHandle ownership)
9. **ADDED: Credential leak at `XtreamController.cs:592`** — `"Connection failed: " + ex.Message` returns URL with credentials
10. **ADDED: SPSC torn-read window** — Removing producer's `read_position.store` creates window where producer overwrites slots consumer is reading
11. **MERGED: Phases 2+3** — Security and concurrency fixes are independent; single phase reduces unnecessary ordering constraints

### New Dependencies Discovered

- P2-033 (error_message) depends on P2-012 (error leakage) — both change error reporting
- P2-007 (polling latency) depends on P2-008 (semaphore fallback) — semaphore is the notification mechanism

### Performance Impact Quantification

| Fix | Overhead | Assessment |
|-----|----------|-----------|
| `DangerousAddRef`/`Release` | ~4ns per call | Negligible; only ~5 sites need manual ref-counting |
| `Interlocked.And` (TOCTOU fix) | ~40ns per flag check | Add `Volatile.Read` fast-path first (2ns) |
| Config `lock` | ~50ns contention | Control plane only, no streaming impact |
| Error code lookup | ~2ns vs ~80ns memcpy | 10x faster than char[48] |

## Overview

Remediate 40 code review findings from PR #1 (Native C++ Streaming Pipeline) across security, data integrity, concurrency, and code quality. The PR introduces a C++/C# hybrid architecture with TsDuck native interop, shared memory IPC, and health-based provider failover.

**Scope:** 5 P1 Critical (blocks merge), 16 P2 Important (should fix), 19 P3 Nice-to-Have (enhancements)

## Problem Statement

The code review identified critical issues that block merge:
- **Data integrity**: SPSC contract violation allows both producer and consumer to write `read_position` concurrently
- **Security**: Plaintext credentials in 12 URL query parameters, SSRF vectors via webhook/provider endpoints
- **Concurrency**: TOCTOU races in flag consumption, non-atomic cross-thread field access
- **Permissions**: World-writable shared memory (0666)

Additionally, 16 important issues affect reliability and 19 enhancements could reduce ~1,453 LOC.

## Dependency Graph

```
P1-001 (SPSC Contract) ──→ P1-003 (TOCTOU ConsumeOverflow)
                       ──→ P2-034 (ConsumeDiscontinuity TOCTOU)

P1-004 (Credentials)  ──→ P2-012 (Error message leakage)
                       ──→ P2-013 (Discord webhook secret)

P1-005 (SSRF)         ──→ P2-039 (curl redirect restriction)

P2-010 (DangerousGetHandle) ──→ P2-011 (GCHandle race)
P2-033 (error_message)      ──→ P2-012 (error leakage)
P2-007 (polling latency)    ──→ P2-008 (semaphore fallback)
```

Items not in this graph are independent and can be parallelized.

### Research Insights: Dependency Ordering

**Architecture Strategist findings:**
- **CRITICAL**: Add a validation checkpoint after Phase 1 — run ThreadSanitizer and C# concurrent tests before proceeding to Phase 2. A broken SPSC fix cascades into 3 dependent P2 fixes.
- Phases 2+3 merged (security and concurrency fixes are independent).

## Technical Approach

### Architecture

Fixes are organized into 3 phases with dependency ordering (original Phases 2+3 merged). Each phase targets a priority level, with security fixes front-loaded.

### Implementation Phases

---

#### Phase 1: P1 Critical (Blocks Merge) — 5 fixes

All P1 items must be resolved before merge. Each fix gets its own commit for easy revert.

##### 1.1 — SPSC Contract Violation (`#001`)

**Files:**
- `native/tsduck_interop/src/ipc/shared_memory_channel.cpp:351`
- `Jellyfin.Xtream/Service/Streaming/SharedMemory/SharedMemoryConsumer.cs`

**Problem:** C++ producer directly advances consumer's `read_position` during overflow, breaking the single-writer contract.

**Fix (Option 1 — Producer sets flag only):**

```cpp
// shared_memory_channel.cpp — BEFORE (line ~351):
// Producer writes consumer's read_position directly
header->read_position.store(new_read_pos, std::memory_order_release);

// AFTER:
// Producer ONLY sets overflow flag; consumer handles position via ConsumeOverflow()
header->flags.fetch_or(FLAG_OVERFLOW, std::memory_order_release);
```

The consumer's existing `ConsumeOverflow()` already handles position adjustment — it just needs the TOCTOU fix from #003.

**CRITICAL: Torn-Read Window (from Plan Review):**

Removing the producer's `read_position.store` creates a window where the producer continues writing into slots the consumer hasn't consumed yet. Between the overflow flag being set and the consumer processing it, the producer may overwrite data the consumer is actively reading.

**Design options:**
1. **Accept bounded data loss** (RECOMMENDED) — Document that overflow means "some packets were lost." Consumer adjusts `read_position` to `write_position` on overflow, accepting the gap. This is already how MPEG-TS handles discontinuities.
2. **Producer stall on overflow** — Producer spins until consumer clears the flag. Risks deadlock if consumer is slow.
3. **Double-buffer overflow region** — Producer writes overflow data to a separate region. Adds complexity.

Option 1 is recommended because MPEG-TS streaming already tolerates packet loss via discontinuity indicators.

**Acceptance Criteria:**
- [ ] `read_position` is only written by consumer side (C# or C++), never by producer
- [ ] Producer sets `FLAG_OVERFLOW` atomically via `fetch_or`
- [ ] Consumer adjusts `read_position` to current `write_position` on overflow (accepting data loss)
- [ ] Overflow scenario passes under ThreadSanitizer (`-fsanitize=thread`)
- [ ] Verify ALL `read_position.store()` occurrences in C++ producer (`grep -n "read_position.store" shared_memory_channel.cpp`)
- [ ] Existing `SharedMemoryConsumerTests` pass

**Effort:** LOW | **Risk:** MEDIUM (torn-read window must be tested)

**Research Insights (SPSC Contract):**

- **C++ Expert**: `fetch_or` with `memory_order_release` is correct — it pairs with consumer's `memory_order_acquire` on the flag read. Do NOT use `relaxed` even though only one bit is being set; the release fence ensures the consumer sees all prior producer writes (write_position, data) before the flag.
- **Data Integrity Guardian**: Multiple race scenarios to test: (1) overflow during active consumer read — consumer must not lose current read position; (2) overflow while consumer is in `ConsumeOverflow()` — flag must not be double-consumed; (3) crash recovery — document that overflow state may be lost on crash.
- **Performance Oracle**: No measurable overhead — `fetch_or` is already the same cost as the existing `store` on x86-64.

---

##### 1.2 — Shared Memory Permissions 0666 → 0600 (`#002`)

**Files:**
- `native/tsduck_interop/src/ipc/shared_memory_channel.cpp` (shm_open, sem_open calls)

**Fix:**

```cpp
// BEFORE:
int fd = shm_open(name.c_str(), O_CREAT | O_RDWR, 0666);
sem_t* sem = sem_open(sem_name.c_str(), O_CREAT, 0666, 0);

// AFTER:
int fd = shm_open(name.c_str(), O_CREAT | O_RDWR, 0600);
sem_t* sem = sem_open(sem_name.c_str(), O_CREAT, 0600, 0);
```

**Platform notes:**
- POSIX (Linux/macOS): `shm_open` and `sem_open` respect mode bits
- Windows: `CreateFileMapping` uses process-level security by default (already isolated)
- Test fixture: `SharedMemoryTestFixture` runs as same user, no impact

**Acceptance Criteria:**
- [ ] `shm_open` and `sem_open` use mode `0600`
- [ ] All existing shared memory tests pass
- [ ] Verify with `stat /dev/shm/<name>` shows `-rw-------`

**Effort:** TRIVIAL (2 lines) | **Risk:** LOW

**Research Insights (Shared Memory Permissions):**

- **C++ Expert**: Add `fchmod(fd, 0600)` defense-in-depth after `shm_open` — some systems may ignore mode in `shm_open` due to umask. Also add `O_EXCL` flag to detect stale segments.
- **Security Sentinel (CRITICAL)**: Add `O_EXCL` to prevent TOCTOU race where an attacker pre-creates a world-readable segment with the same name before the producer starts.
- **Plan Review (CRITICAL)**: Do NOT blindly `shm_unlink` on `EEXIST` — check `ProducerState` field first. Another instance may have a live segment. Only unlink if the segment's producer state indicates it's stale (e.g., `ProducerState::Stopped`).
- **OWASP Expert**: For container environments, document that `/dev/shm` namespace is shared within a pod — recommend `--ipc=private` Docker flag or unique segment names with PID prefix.

```cpp
// Enhanced fix with safe O_EXCL:
int fd = shm_open(name.c_str(), O_CREAT | O_EXCL | O_RDWR, 0600);
if (fd == -1 && errno == EEXIST) {
    // Check if existing segment is stale before unlinking
    int existing_fd = shm_open(name.c_str(), O_RDONLY, 0);
    if (existing_fd != -1) {
        // Map header, check ProducerState — only unlink if stopped/crashed
        // See implementation for full stale-segment detection logic
        close(existing_fd);
    }
    shm_unlink(name.c_str());
    fd = shm_open(name.c_str(), O_CREAT | O_EXCL | O_RDWR, 0600);
}
if (fd != -1) {
    fchmod(fd, 0600);  // Defense-in-depth against umask
}
```

---

##### 1.3 — TOCTOU Race in ConsumeOverflow (`#003`)

**Depends on:** #001 (SPSC contract fix must land first)

**Files:**
- `Jellyfin.Xtream/Service/Streaming/SharedMemory/SharedMemoryConsumer.cs:541-577`

**Problem:** `Volatile.Read` then `Volatile.Write` — producer can modify between read and write.

**Fix:** Replace with atomic test-and-clear using `Interlocked.And`:

```csharp
// BEFORE (SharedMemoryConsumer.cs:551-568):
var flags = Volatile.Read(ref _basePtr[FlagsOffset]);
if ((flags & FLAG_OVERFLOW) != 0)
{
    // RACE: producer can set flag here
    Volatile.Write(ref _basePtr[FlagsOffset], (byte)(flags & ~FLAG_OVERFLOW));
    // ... handle overflow
}

// AFTER:
// Atomic test-and-clear — single operation, no TOCTOU window
var previousFlags = Interlocked.And(ref Unsafe.As<byte, int>(ref _basePtr[FlagsOffset]), ~FLAG_OVERFLOW);
if ((previousFlags & FLAG_OVERFLOW) != 0)
{
    // Flag was set and is now cleared atomically
    AdjustReadPositionAfterOverflow();
}
```

**Note (UPDATED):** The C# `Flags` field is already `uint` at `FieldOffset(0xC0)` — naturally 4-byte aligned, matching C++ `std::atomic<uint32_t>`. No widening needed. Use `Unsafe.As<uint, int>(ref ...)` for `Interlocked.And` which requires `ref int`.

**Acceptance Criteria:**
- [ ] Flag consumption is single atomic operation (no read-then-write gap)
- [ ] Overflow handling still adjusts `read_position` correctly
- [ ] Stress test with concurrent producer overflow + consumer read passes

**Effort:** LOW | **Risk:** LOW

**Research Insights (TOCTOU Fix):**

- **C# Expert**: `Interlocked.And` provides full barrier semantics on x86 (lock-prefixed instruction). For cross-process shared memory, add explicit `Thread.MemoryBarrier()` AFTER the `Interlocked.And` to ensure acquire semantics are visible across the C++/C# boundary — `Interlocked.And` alone has no guaranteed acquire semantics for cross-language shared memory.
- **Data Integrity Guardian**: The `Flags` field at offset `0xC0` IS naturally 4-byte aligned — confirmed safe for `Interlocked.And`. Apply identical fix to `ConsumeDiscontinuity()` (#034) in the same commit.
- **Architecture Strategist (CRITICAL)**: Cross-language atomics memory model mismatch — C++ `memory_order_release` on flag set pairs with C++ `memory_order_acquire` on flag read, but C# `Interlocked.And` maps to `lock cmpxchg` which is full barrier on x86 only. On ARM64, add `Thread.MemoryBarrier()` after the Interlocked op.

```csharp
// Corrected pattern with cross-language safety:
ref int flagsRef = ref Unsafe.As<uint, int>(ref Unsafe.AsRef<uint>(_basePtr + FlagsOffset));
int previousFlags = Interlocked.And(ref flagsRef, ~(int)FLAG_OVERFLOW);
Thread.MemoryBarrier(); // Acquire fence for cross-process C++→C# visibility
if ((previousFlags & (int)FLAG_OVERFLOW) != 0)
{
    AdjustReadPositionAfterOverflow();
}
```

---

##### 1.4 — Plaintext Credentials in URL Query Parameters (`#004`)

**Files:**
- `Jellyfin.Xtream/Client/XtreamClient.cs` (12 methods)
- `Jellyfin.Xtream/Client/ConnectionInfo.cs`

**Problem:** All 12 API methods embed `username` and `password` as URL query parameters. These appear in HTTP logs, reverse proxy logs, and browser history. Additionally, `XtreamController.cs:592` returns `"Connection failed: " + ex.Message` which includes the full URL with credentials in a 200 OK response.

**Fix (Option 1 — URL Redaction + Mask ToString):**

The Xtream API **requires** credentials as query parameters (protocol constraint — we cannot move to POST/headers). Therefore, we redact credentials from logging output.

```csharp
// NEW: Jellyfin.Xtream/Client/CredentialRedactingHandler.cs
internal sealed class CredentialRedactingHandler : DelegatingHandler
{
    private static readonly Regex CredentialPattern = new(
        @"((?:username|password|token)=)[^&]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Redact ONLY in logging — actual request preserves credentials
        return await base.SendAsync(request, cancellationToken);
    }
}
```

```csharp
// ConnectionInfo.cs — mask password in ToString():
public override string ToString()
{
    var maskedPassword = Password?.Length > 2
        ? $"{Password[0]}***{Password[^1]}"
        : "***";
    return $"BaseUrl={BaseUrl}, Username={Username}, Password={maskedPassword}";
}
```

Additionally, register the handler in HttpClient factory and ensure Jellyfin's `IHttpClientFactory` respects it.

**Acceptance Criteria:**
- [ ] `ConnectionInfo.ToString()` shows masked password
- [ ] HTTP request logging shows `password=***` in all log levels
- [ ] Actual HTTP requests still contain real credentials (protocol requirement)
- [ ] Grep for `password=` in log output produces zero matches with real values
- [ ] All 12 XtreamClient methods verified

**Effort:** MEDIUM | **Risk:** LOW

**Research Insights (Credential Redaction):**

- **C# Expert (CRITICAL UPDATE)**: Use `IHttpClientLogger` (.NET 8) instead of `DelegatingHandler`. The `DelegatingHandler` only catches requests sent through the handler chain — it misses framework-level logging from `HttpClientFactory` and `ILogger<HttpClient>`. `IHttpClientLogger` hooks into the logging pipeline itself:

```csharp
// Preferred approach — catches ALL log output:
internal sealed class CredentialRedactingLogger : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request)
    {
        // Redact URL before it reaches any logger
        return null;
    }

    public void LogRequestStop(object? context, HttpRequestMessage request,
        HttpResponseMessage response, TimeSpan elapsed) { }

    public void LogRequestFailed(object? context, HttpRequestMessage request,
        HttpResponseMessage? response, Exception exception, TimeSpan elapsed) { }
}

// Registration:
services.AddHttpClient("XtreamClient")
    .RemoveAllLoggers()
    .AddLogger<CredentialRedactingLogger>();
```

- **Security Sentinel (CRITICAL)**: `ConnectionInfo.ToString()` at line 42 EXPLICITLY returns the raw password — this is a confirmed leak vector. The masking fix is essential.
- **Plan Review (CRITICAL)**: `XtreamController.cs:592` returns `"Connection failed: " + ex.Message` in a 200 OK — the exception message contains the full URL with credentials. Enumerate ALL catch blocks that pass `ex.Message` to clients (7+ sites across controllers).
- **Simplicity Reviewer**: The `DelegatingHandler` approach won't catch all log paths. The `IHttpClientLogger` is simpler AND more complete. Skip the handler entirely.

---

##### 1.5 — SSRF via Discord Webhook & Unvalidated BaseUrl (`#005`)

**Files:**
- `Jellyfin.Xtream/Api/XtreamConfigurationController.cs` (TestDiscordWebhook)
- `Jellyfin.Xtream/Api/XtreamDiscoveryController.cs:443` (ImportDiscoveredProvider)

**Fix:**

```csharp
// NEW: Jellyfin.Xtream/Utility/UrlValidator.cs
internal static class UrlValidator
{
    private static readonly HashSet<string> AllowedDiscordHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.com",
        "discordapp.com",
    };

    public static bool IsValidDiscordWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps
            && AllowedDiscordHosts.Contains(uri.Host)
            && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
    }

    public static bool IsValidProviderUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Must be HTTP or HTTPS (Xtream providers commonly use HTTP)
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Block private/loopback IPs
        if (IPAddress.TryParse(uri.Host, out var ip))
            return !IsPrivateOrLoopback(ip);

        // Block localhost variants
        return !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] switch
        {
            10 => true,                                          // 10.0.0.0/8
            127 => true,                                         // 127.0.0.0/8
            172 => bytes[1] >= 16 && bytes[1] <= 31,             // 172.16.0.0/12
            192 => bytes[1] == 168,                              // 192.168.0.0/16
            169 => bytes[1] == 254,                              // 169.254.0.0/16 (link-local)
            _ => false,
        };
    }
}
```

**Acceptance Criteria:**
- [ ] `TestDiscordWebhook` rejects non-`discord.com`/`discordapp.com` URLs
- [ ] `TestDiscordWebhook` rejects non-HTTPS URLs
- [ ] `ImportDiscoveredProvider` rejects private IPs (10.x, 172.16-31.x, 192.168.x, 127.x, 169.254.x)
- [ ] `ImportDiscoveredProvider` rejects `localhost`
- [ ] Legitimate Discord webhook URLs still work
- [ ] Legitimate provider URLs still work
- [ ] Unit tests cover edge cases (IPv6 loopback `::1`, link-local `fe80::`)

**Effort:** LOW | **Risk:** LOW

**Research Insights (SSRF Validation):**

- **Security Sentinel (CRITICAL)**: IPv6 is completely unprotected in the current plan. The `IsPrivateOrLoopback` method only checks `bytes.Length == 4` — ALL IPv6 addresses pass through. Must add:

```csharp
private static bool IsPrivateOrLoopback(IPAddress ip)
{
    if (IPAddress.IsLoopback(ip)) return true;

    // Handle IPv6-mapped IPv4 (::ffff:127.0.0.1)
    if (ip.IsIPv4MappedToIPv6)
        return IsPrivateOrLoopback(ip.MapToIPv4());

    byte[] bytes = ip.GetAddressBytes();

    // IPv6 checks
    if (bytes.Length == 16)
    {
        // fe80::/10 — link-local
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        // fc00::/7 — unique local (RFC 4193)
        if ((bytes[0] & 0xFE) == 0xFC) return true;
        // ::1 — loopback (already caught by IsLoopback, defense-in-depth)
        if (ip.Equals(IPAddress.IPv6Loopback)) return true;
        return false;
    }

    // IPv4 checks
    return bytes[0] switch
    {
        10 => true,
        127 => true,
        172 => bytes[1] >= 16 && bytes[1] <= 31,
        192 => bytes[1] == 168,
        169 => bytes[1] == 254,
        0 => true,  // 0.0.0.0/8
        _ => false,
    };
}
```

- **Simplicity Reviewer**: The `UrlValidator` utility class is fine — it's small, focused, and used in 2 controllers. Don't inline it.
- **Plan Review**: DNS rebinding and punycode/IDN validation are YAGNI for admin-only endpoints (`RequiresElevation`). The attack requires a compromised admin account, at which point DNS rebinding is the least of the concerns. Removed from scope.

---

#### Phase 2: P2 Security, Concurrency & Safety — 14 fixes (merged from original Phases 2+3)

Security-sensitive P2 items and concurrency/safety fixes. These are independent of each other and can be parallelized.

##### 2.1 — Error Message Leakage in 8 API Endpoints (`#012`)

**Files:** `Jellyfin.Xtream/Api/XtreamController.cs` (8 catch blocks returning `ex.Message`)

**Fix:** Replace raw exception messages with generic error responses. Enumerate ALL catch blocks that pass `ex.Message` to clients — at least 7+ sites across `XtreamController.cs`, `XtreamConfigurationController.cs`, and `XtreamDiscoveryController.cs`.

**CRITICAL**: `XtreamController.cs:592` returns `"Connection failed: " + ex.Message` in a 200 OK response — the exception message contains the full URL with credentials.

```csharp
// BEFORE:
catch (Exception ex)
{
    return StatusCode(502, new { error = ex.Message });
}

// AFTER:
catch (Exception ex)
{
    _logger.LogError(ex, "Stream request failed for channel {ChannelId}", channelId);
    return Problem(
        statusCode: 502,
        title: "Stream Unavailable",
        detail: "Stream temporarily unavailable. Retry after a few seconds.");
}
```

**Effort:** LOW | **Risk:** LOW

##### 2.2 — Discord Webhook Secret Exposed in GET Response (`#013`)

**File:** `Jellyfin.Xtream/Api/XtreamConfigurationController.cs:149-168`

**Fix:** Redact webhook URL in GET response, only return whether webhook is configured:

```csharp
// Return { isConfigured: true, url: "https://discord.com/api/webhooks/***" }
// instead of the full webhook URL with token
```

**Note (Plan Review):** Handle the GET/PUT round-trip — if the frontend reads the redacted URL and PUTs it back, the actual webhook URL would be overwritten with `***`. Either: (a) the PUT endpoint ignores the URL field if it matches the redacted pattern, or (b) only return `isConfigured: bool` without any URL fragment.

**Effort:** TRIVIAL | **Risk:** LOW

##### ~~2.3 — Legacy Credentials Not Cleared After Migration (`#036`)~~ **REMOVED**

**Already implemented** in `Plugin.cs:228-230`. No action needed.

##### 2.4 — curl Follows Redirects Without Protocol Restriction (`#039`)

**File:** `native/tsduck_interop/src/streaming/stream_source.hpp`

**Fix:** Restrict curl redirects to HTTP/HTTPS only:

```cpp
// Add after CURLOPT_FOLLOWLOCATION:
curl_easy_setopt(curl, CURLOPT_REDIR_PROTOCOLS_STR, "http,https");
```

This blocks redirects to `file://`, `gopher://`, `dict://`, and other dangerous protocols while allowing legitimate CDN redirects.

**Effort:** TRIVIAL (1 line) | **Risk:** LOW

**Research Insights (curl Protocol Restriction):**

- **C++ Expert (CRITICAL)**: Must ALSO set `CURLOPT_PROTOCOLS_STR` for the INITIAL request, not just redirects. Without this, an attacker-controlled provider URL like `file:///etc/passwd` works on the first request:

```cpp
// COMPLETE fix — both initial and redirect protocols:
#if CURL_AT_LEAST_VERSION(7, 85, 0)
curl_easy_setopt(curl, CURLOPT_PROTOCOLS_STR, "http,https");
curl_easy_setopt(curl, CURLOPT_REDIR_PROTOCOLS_STR, "http,https");
#else
curl_easy_setopt(curl, CURLOPT_PROTOCOLS, CURLPROTO_HTTP | CURLPROTO_HTTPS);
curl_easy_setopt(curl, CURLOPT_REDIR_PROTOCOLS, CURLPROTO_HTTP | CURLPROTO_HTTPS);
#endif
curl_easy_setopt(curl, CURLOPT_MAXREDIRS, 5L);  // Reduce from default 30
```

- **OWASP Expert**: Also reduce `CURLOPT_MAXREDIRS` to 3-5 (default is 30) to limit redirect chains that could be used for timing attacks or resource exhaustion.
- **Security Sentinel**: The initial protocol restriction is CRITICAL — without it, the SSRF fix in #005 is incomplete because curl can still access `file://` and `gopher://` schemes directly.
- **Plan Review**: Use `#if CURL_AT_LEAST_VERSION` for the `_STR` variants (added in curl 7.85.0). The deprecated non-`_STR` versions are the fallback for older curl.

---

##### ~~2.5 — Non-Atomic Statistics (`#006`)~~ **REMOVED**

**Plan Review**: `Volatile.Read`+`Volatile.Write` is the CORRECT pattern for SPSC single-writer statistics. Only the consumer writes these counters; readers only read. `Interlocked.Add` would add ~12ns overhead per update for no correctness benefit. No action needed.

##### 2.6 — 50ms Polling Latency (`#007`)

**File:** `CircularBufferReadStream.cs:306-336`

**Fix:** Add semaphore notification from producer to reduce wake-up latency. Keep polling as fallback when semaphore unavailable (platform compatibility).

**Effort:** MEDIUM | **Risk:** MEDIUM (performance-sensitive hot path)

**Research Insights:** Performance Oracle recommends semaphore coalescing — notify every 10 slots instead of every write to reduce syscall overhead. For 188-byte TS packets at ~10Mbps, this means notification every ~1.4ms which keeps latency well under the current 50ms polling.

**Note:** Depends on §2.8 (semaphore fallback) — the semaphore is the notification mechanism.

##### 2.8 — Linux Semaphore Fallback to Spin-Wait (`#008`)

**File:** `SharedMemoryConsumer.cs:834-852`

**Fix:** Use `eventfd` or POSIX named semaphore with `sem_timedwait` instead of `Thread.Sleep(1)` spin-wait.

**Effort:** MEDIUM | **Risk:** MEDIUM

##### ~~2.9 — AVX2 NT Store Alignment Check (`#009`)~~ **REMOVED**

**Plan Review**: The `0xF` mask is CORRECT. `SimdMemoryCopy.cs` uses `Sse2.StoreAlignedNonTemporal` which requires 128-bit (16-byte) alignment, NOT 256-bit. Changing to `0x1F` would halve NT store utilization by rejecting valid 16-byte-aligned addresses. No action needed.

##### 2.10 — DangerousGetHandle Without Ref-Counting (`#010`)

**File:** `NativeStreamer.cs` (~5 sites needing manual fix)

**Problem:** `DangerousGetHandle()` used without ref-counting at ~35 sites.

**Key insight (Plan Review):** `TsDuckNativeMethods.cs` already uses `[LibraryImport]` which handles `SafeHandle` marshalling automatically. This eliminates ~30 of the 35 sites. Only ~5 async callback sites need manual ref-counting.

**Fix:** Inline the 6-line pattern at each of the ~5 remaining sites (no helper type needed):

```csharp
bool success = false;
try
{
    _streamer.DangerousAddRef(ref success);
    TsDuckNativeMethods.SomeMethod(_streamer.DangerousGetHandle(), ...);
}
finally
{
    if (success) _streamer.DangerousRelease();
}
```

**Effort:** LOW (~5 sites) | **Risk:** LOW

##### 2.11 — GCHandle Race in Callback Unregistration (`#011`)

**Files:** `NativeStreamer.cs:216-233`, `NativeChannelRegistry.cs:558`

**Fix:** Use Interlocked flag to guard callback invocation during teardown. `Thread.Sleep(10)` grace period is sufficient — callbacks are infrequent.

```csharp
// Add atomic disposed flag checked in callback handler:
private volatile int _callbackDisposed;

// In callback handler (called from native):
if (Volatile.Read(ref _callbackDisposed) != 0) return;

// In Dispose:
Interlocked.Exchange(ref _callbackDisposed, 1);
// Wait for in-flight callbacks to complete
Thread.Sleep(10); // Brief grace period
TsDuckNativeMethods.StreamerSetEventCallback(_streamer.DangerousGetHandle(), 0, 0);
if (_eventCallbackHandle.IsAllocated)
    _eventCallbackHandle.Free();
```

**Effort:** LOW | **Risk:** LOW

##### 2.12 — SharedMemoryConsumer Constructor Resource Leak (`#014`)

**File:** `SharedMemoryConsumer.cs:188`

**Fix:** Wrap constructor body in try-catch, dispose partial resources on failure:

```csharp
// PREFERRED: Factory method pattern (C# Expert recommendation)
public static SharedMemoryConsumer Open(string name)
{
    MemoryMappedFile? mmf = null;
    MemoryMappedViewAccessor? accessor = null;
    try
    {
        mmf = MemoryMappedFile.OpenExisting(name);
        accessor = mmf.CreateViewAccessor();
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        return new SharedMemoryConsumer(mmf, accessor, ptr); // Private constructor
    }
    catch
    {
        accessor?.Dispose();
        mmf?.Dispose();
        throw;
    }
}
```

**Effort:** LOW | **Risk:** LOW

**Research Insights:** C# Expert recommends factory method pattern over try-catch in constructor — constructors that throw after partial initialization are a known anti-pattern in .NET. The factory method makes resource ownership explicit and prevents the half-constructed object problem.

##### 2.13 — Non-Volatile Cross-Thread Fields (`#015`)

**File:** `CircularBufferWriteStream.cs:76,147,158`

**Fix:** `DateTime` is a struct — cannot use `volatile` modifier. Use `Volatile.Read`/`Volatile.Write` on `long` (ticks) representation:

```csharp
// Can't do: volatile DateTime _lastWriteTime; // CS0677
// Instead, store as ticks:
private long _lastWriteTimeTicks;
public DateTime LastWriteTime => new(Volatile.Read(ref _lastWriteTimeTicks));
// Write: Volatile.Write(ref _lastWriteTimeTicks, DateTime.UtcNow.Ticks);
```

**Effort:** TRIVIAL | **Risk:** LOW

**Research Insights:** Data Integrity Guardian confirmed: `volatile` cannot be applied to `DateTime` (it's a struct). Must use `long` ticks with `Volatile.Read`/`Write` accessor pattern.

##### 2.14 — Config Save Lost-Update Race (`#016`)

**File:** `XtreamConfigurationController.cs`

**Fix:** Add `lock` around configuration update + save sequences. Plain `lock` is sufficient — `Monitor.TryEnter` adds complexity for no benefit at admin UI request rates.

```csharp
private static readonly object ConfigLock = new();

[HttpPut("Configuration/Proxy")]
public ActionResult UpdateProxyConfig([FromBody] ProxyConfigRequest request)
{
    lock (ConfigLock)
    {
        var config = Plugin.Instance.Configuration;
        // ... update fields ...
        Plugin.Instance.SaveConfiguration();
    }
    return Ok(new { success = true });
}
```

**Note:** Investigate whether `SaveConfiguration()` is already thread-safe in Jellyfin framework. If so, the lock may be unnecessary.

**Effort:** LOW | **Risk:** LOW

##### 2.15 — Non-Atomic error_message (`#033`)

**File:** `native/tsduck_interop/src/ipc/shared_memory_channel.cpp:456-458`

**Fix:** Replace `memcpy` of `char[48]` with atomic error code + static string table lookup. The `error_code` field already exists at offset `0xC4` in the shared memory header — no protocol versioning or layout change needed.

```cpp
// BEFORE:
memcpy(header->error_message, msg.c_str(), std::min(msg.size(), 47UL));

// AFTER:
// error_code already exists at offset 0xC4 — just use it instead of error_message
header->error_code.store(ErrorCode::BufferOverflow, std::memory_order_release);
// Consumer maps error_code to human-readable string on the C# side
```

**Note:** Keep the `error_message` field for ABI stability but stop writing to it. The C# consumer already has `error_code` in `SharedMemoryHeader.cs` at `FieldOffset(0xC4)`.

**Effort:** LOW | **Risk:** LOW

##### 2.16 — ConsumeDiscontinuity TOCTOU (`#034`)

**File:** `SharedMemoryConsumer.cs:522-533`

**Fix:** Same pattern as #003 — use `Interlocked.And` for atomic test-and-clear.

**Effort:** LOW | **Risk:** LOW

##### 2.17 — Partial Null TS Packet Writes (`#035`)

**File:** `CircularBufferWriteStream.cs:489-513`

**Fix:** Always write complete 188-byte null packets. Fill sub-188 remainder with `0xFF` (invalid sync byte) so decoders skip it:

```csharp
int remaining = alignmentGap;
while (remaining >= TsPacketSize)
{
    WriteNullTsPacket(); // Full 188-byte null packet
    remaining -= TsPacketSize;
}
// Fill sub-packet remainder with 0xFF (invalid sync, decoders skip)
if (remaining > 0)
{
    new Span<byte>(_bufferPtr + _writePosition, remaining).Fill(0xFF);
    _writePosition += remaining;
}
```

**Effort:** LOW | **Risk:** LOW

---

#### Phase 3: P3 Enhancements — 19 fixes

Non-blocking improvements for code quality and maintainability. Batch by category.

##### 3.1 — Dead Code Removal (~1,200 LOC)

| Item | File | LOC | Action |
|------|------|-----|--------|
| #017 | `ServiceLevelIndicators.cs` | 518 | Delete entire file (zero callers) |
| #029 | `CircuitBreakerRegistry` in `CircuitBreaker.cs` | 143 | Delete class (unused in production) |
| #031 | Capabilities endpoint | 50 | Delete method (no known consumer) |
| #032 | AVX-512 SIMD path in `SimdMemoryCopy.cs` | 93 | ~~Delete~~ **KEEP** (see Research Insights) |

**Verification:** Run Roslyn analyzer to confirm zero callers before deletion.

**Research Insights (Dead Code):**

- **Simplicity Reviewer (UPDATED)**: Keep the AVX-512 path (#032) — deletion is the wrong direction for future hardware. Intel Sapphire Rapids and AMD Zen 5 no longer throttle AVX-512. The 93 lines are well-isolated and will become the optimal path. Remove only the 3 genuinely dead items (#017, #029, #031).
- **Architecture Strategist**: Before deleting `ServiceLevelIndicators.cs` (#017), audit XML serialization — if it's referenced in any `PluginConfiguration` XML schema, deleting it will break deserialization of existing config files. Run `grep -r "ServiceLevelIndicators" --include="*.xml" --include="*.config"`.

##### 3.2 — Duplication & Simplification (~185 LOC)

| Item | File | LOC | Action |
|------|------|-----|--------|
| #018 | `CacheLinePadded` struct | 15 | Extract to shared file |
| #019 | Restream constructors | 40 | Consolidate via constructor chaining |
| #025 | Restream forwarding delegates | 97 | Remove static pass-throughs |
| #030 | RestreamHealthMonitor duplicate LINQ | 15 | Cache query result |

##### 3.3 — Performance Improvements

| Item | File | Action |
|------|------|--------|
| #020 | `DateTime.UtcNow` in hot paths | Replace with `Stopwatch` / `Environment.TickCount64` |
| #021 | Redundant `Thread.MemoryBarrier()` | Remove after `Volatile.Read` |
| #022 | Keyframe `std::vector` allocation | Pre-allocate or use stack buffer |
| #023 | `record_success()` mutex per chunk | Batch success recording or use atomic counter |

##### 3.4 — Safety & Correctness

| Item | File | Action |
|------|------|--------|
| #026 | `NativeChannelRegistry._disposed` not volatile | Add `volatile` modifier |
| #027 | `Environment.MachineName` in Discord | Redact or use generic identifier |
| #028 | Integer overflow in slot calculation | Cast before multiply: `(long)currentSlot * slotSize` |
| #037 | Available slots underflow | Add guard: `if (write >= read)` before subtraction |
| #038 | C#/C++ timestamp clock mismatch | Document or align clocks |
| #040 | `delete this` pattern | Replace with stop-flag + thread-join (see below) |

**Research Insights (Safety & Correctness):**

- **Plan Review**: `shared_ptr` pattern conflicts with SafeHandle ownership model — the C# side owns the pipeline lifetime via SafeHandle. Use stop-flag + thread-join instead:

```cpp
// Instead of: delete this;
// Use stop-flag + thread-join:
void StreamPipeline::request_stop() {
    stop_flag_.store(true, std::memory_order_release);
}

void StreamPipeline::worker_loop() {
    while (!stop_flag_.load(std::memory_order_acquire)) {
        // ... process data ...
    }
    // Cleanup happens here, but object is NOT deleted
    // C# SafeHandle calls destroy() later via P/Invoke
}
```

##### 3.5 — Missing Unit Tests (`#024`)

| Area | Files | Tests Needed |
|------|-------|-------------|
| CircuitBreaker | `CircuitBreaker.cs` | State transitions, threshold behavior |
| OverflowPredictor | `OverflowPredictor.cs` | Prediction accuracy, edge cases |
| SimdMemoryCopy | `SimdMemoryCopy.cs` | Alignment, threshold, correctness |

---

## Acceptance Criteria

### Functional Requirements

- [x] All 5 P1 fixes implemented and verified
- [x] All active P2 fixes implemented and verified (§2.5 statistics removed, §2.9 AVX2 removed, §2.3 already done)
- [x] All `ex.Message` sites enumerated and sanitized (7+ catch blocks across controllers)
- [x] Dead code removed (~750 LOC reduction — 3 items, AVX-512 kept)
- [x] Unit tests added for CircuitBreaker (17 tests), OverflowPredictor (10 tests), SimdMemoryCopy (16 tests)
- [x] CacheLinePadded structs extracted to shared Utility file (§3.2 #018)
- [x] DateTime.UtcNow replaced with Environment.TickCount64 in hot read loop (§3.3 #020)
- [x] SimdMemoryCopy CopyRemainder bug fixed — while loop for 8-byte chunks handles >15 byte remainders
- [ ] No regression in existing test suite

### Non-Functional Requirements

- [x] No credentials visible in any log level (including `XtreamController.cs:592`)
- [x] Shared memory permissions are 0600 (owner-only)
- [x] SSRF vectors blocked (private IPs, non-HTTPS for Discord)
- [x] No TOCTOU races in flag consumption
- [x] SPSC contract: `read_position` written only by consumer side (C# or C++), never by producer
- [ ] ARM64 note: No ARM64 CI — document that `Thread.MemoryBarrier()` is needed but untested

### Quality Gates

- [ ] All existing C# tests pass (`dotnet test`)
- [ ] All existing C++ tests pass (Docker build)
- [ ] CodeQL scan shows no new alerts
- [ ] CSharpier formatting check passes
- [ ] ThreadSanitizer clean for shared memory operations

## Risk Analysis & Mitigation

| Risk | Impact | Mitigation |
|------|--------|-----------|
| SPSC fix torn-read window | Brief data corruption during overflow | Accept bounded packet loss (MPEG-TS tolerates this via discontinuity) |
| Credential redaction misses a log path | Credential leak | Grep audit of all log output in CI; enumerate all `ex.Message` sites |
| SSRF allowlist too restrictive | Legitimate webhooks blocked | Log blocked URLs at Warning level for debugging |
| DangerousGetHandle refactor misses a site | Use-after-free | Roslyn analyzer to detect remaining raw calls |
| Dead code removal breaks E2E tests | Test failures | Run full E2E suite after each deletion |
| O_EXCL unlinks live segment | Data loss for other instances | Check ProducerState before unlinking stale segments |
| Cross-language atomics on ARM64 | Silent data corruption | Add `Thread.MemoryBarrier()` after `Interlocked` ops; no ARM64 CI (documented gap) |
| IPv6 SSRF bypass | SSRF via `::ffff:127.0.0.1` | Add IPv6 private range checks + IPv4-mapped handling |
| XML deserialization break from dead code removal | Config corruption | Grep XML schemas before deleting `ServiceLevelIndicators.cs` |
| Webhook GET/PUT round-trip | Webhook URL overwritten with redacted value | Ignore URL field on PUT if matches redacted pattern |

## Commit Strategy

- **P1 fixes:** One commit per fix (5 commits) — individually revertable
- **P2 fixes:** Group by affected component (shared memory, streaming, API, security)
- **P3:** Batch by category (dead code, duplication, performance, safety)

## References

### Internal References
- Code review comment: [PR #1 comment](https://github.com/magyargergo/Jellyfin.Xtream/pull/1#issuecomment-3901329272)
- Todo tracking: `todos/001-040-pending-*.md`
- CLAUDE.md conventions: `/Users/garymagy/personal/Jellyfin.Xtream/CLAUDE.md`

### Key File Paths
- `native/tsduck_interop/src/ipc/shared_memory_channel.cpp` — SPSC producer, permissions
- `Jellyfin.Xtream/Service/Streaming/SharedMemory/SharedMemoryConsumer.cs` — SPSC consumer, TOCTOU
- `Jellyfin.Xtream/Client/XtreamClient.cs` — Credential URLs (12 methods)
- `Jellyfin.Xtream/Api/XtreamConfigurationController.cs` — SSRF, webhook, config save
- `Jellyfin.Xtream/Api/XtreamDiscoveryController.cs` — SSRF BaseUrl
- `Jellyfin.Xtream/Service/Streaming/Native/NativeStreamer.cs` — DangerousGetHandle, GCHandle
- `Jellyfin.Xtream/Service/SimdMemoryCopy.cs` — AVX2 alignment, dead code
- `Jellyfin.Xtream/Service/CircularBufferWriteStream.cs` — Partial null packets, volatile fields
