// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_METRICS_METRICS_DATABASE_HPP
#define TSDUCK_INTEROP_METRICS_METRICS_DATABASE_HPP

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

// Conditional compilation for SQLite support
#ifndef TSDUCK_HAS_SQLITE
#define TSDUCK_HAS_SQLITE 0
#endif

#if TSDUCK_HAS_SQLITE
// Forward declaration for SQLite (avoid including sqlite3.h in header)
struct sqlite3;
struct sqlite3_stmt;
#endif

namespace tsduck_interop::metrics {

// ============================================================================
// Provider Health Structure (blittable for C# P/Invoke)
// ============================================================================

/// Provider health metrics aggregated from the database.
/// This structure is designed to be blittable for direct P/Invoke marshalling.
struct ProviderHealth {
    int32_t url_index;              // Provider URL index (0-based)
    double health_score;            // Computed health score (0.0-100.0)
    int64_t total_bytes;            // Total bytes received from this provider
    int32_t success_count;          // Number of successful data receipts
    int32_t failure_count;          // Number of failures
    int64_t avg_latency_ms;         // Average connection latency in milliseconds
    int64_t last_success_time;      // Unix timestamp of last successful data
    int64_t last_failure_time;      // Unix timestamp of last failure
    int32_t consecutive_failures;   // Current consecutive failure count
    int32_t reserved;               // Padding for alignment
};

// ============================================================================
// Failure Type Enumeration
// ============================================================================

/// Categorizes the type of failure for better diagnostics and scoring.
enum class FailureType : int32_t {
    Unknown = 0,            // Unclassified failure
    ConnectionTimeout = 1,  // TCP/TLS handshake timeout
    HttpError = 2,          // HTTP 4xx/5xx response
    NetworkError = 3,       // Connection reset, DNS failure, etc.
    DataStall = 4,          // No data received within timeout
    QualityDegraded = 5,    // TR 101 290 error threshold exceeded
    Disconnected = 6,       // Clean disconnect by server
    InternalError = 7       // Internal processing error
};

// ============================================================================
// Metrics Database Class
// ============================================================================

/// SQLite-based metrics storage for provider health tracking.
///
/// This class provides thread-safe persistent storage of streaming metrics,
/// enabling C++ to own all health tracking without reporting to C#.
/// C# can query the database directly if needed.
///
/// Database schema:
///   - provider_metrics: Individual success/failure events with timestamps
///   - provider_health: Aggregated health scores and statistics
///
/// Thread safety:
///   - All public methods are mutex-protected
///   - Safe for concurrent access from streaming and query threads
///
/// Usage:
///   MetricsDatabase db("/path/to/metrics.db");
///   db.record_success(0, 85.5, 65536, 150);  // url_index=0, quality=85.5, bytes=64KB, latency=150ms
///   db.record_failure(0, FailureType::DataStall, "No data for 20 seconds");
///   auto health = db.get_provider_health(0);
class MetricsDatabase {
public:
    /// Construct and open/create the database at the specified path.
    /// @param db_path Path to SQLite database file. Created if not exists.
    /// @throws std::runtime_error if database cannot be opened or created.
    explicit MetricsDatabase(const std::string& db_path);

    /// Destructor closes the database connection.
    ~MetricsDatabase();

    // Non-copyable, non-movable (owns database connection)
    MetricsDatabase(const MetricsDatabase&) = delete;
    MetricsDatabase& operator=(const MetricsDatabase&) = delete;
    MetricsDatabase(MetricsDatabase&&) = delete;
    MetricsDatabase& operator=(MetricsDatabase&&) = delete;

    // ========================================================================
    // Recording Methods (called from streaming thread)
    // ========================================================================

    /// Record a successful data receipt event.
    /// @param url_index Index of the provider URL (0-based).
    /// @param url The provider URL string (for display/debugging).
    /// @param quality_score Stream quality score (0.0-100.0, based on TR 101 290).
    /// @param bytes Number of bytes received in this chunk.
    /// @param latency_ms Connection latency in milliseconds.
    void record_success(int32_t url_index,
                       const std::string& url,
                       double quality_score,
                       int64_t bytes,
                       int64_t latency_ms);

    /// Record a failure event.
    /// @param url_index Index of the provider URL (0-based).
    /// @param url The provider URL string.
    /// @param failure_type Category of the failure.
    /// @param error_msg Detailed error message.
    void record_failure(int32_t url_index,
                       const std::string& url,
                       FailureType failure_type,
                       const std::string& error_msg);

    // ========================================================================
    // Query Methods (can be called from any thread, including C# via P/Invoke)
    // ========================================================================

    /// Get aggregated health metrics for a specific provider.
    /// @param url_index Index of the provider URL.
    /// @param out_health Output structure to receive health data.
    /// @return true if provider exists and health was retrieved.
    bool get_provider_health(int32_t url_index, ProviderHealth* out_health);

    /// Get health metrics for all known providers.
    /// @return Vector of health structures for all providers.
    std::vector<ProviderHealth> get_all_provider_health();

    /// Get the number of known providers in the database.
    int32_t get_provider_count();

    // ========================================================================
    // Housekeeping Methods
    // ========================================================================

    /// Remove old metric records to prevent unbounded database growth.
    /// @param max_age_seconds Maximum age of records to keep.
    void cleanup_old_records(int64_t max_age_seconds);

    /// Reset all health scores to default values.
    /// Useful when starting a new streaming session.
    void reset_all_health_scores();

    /// Reset health score for a specific provider.
    /// @param url_index Index of the provider URL.
    void reset_provider_health(int32_t url_index);

    /// Get the database file path.
    [[nodiscard]] const std::string& db_path() const noexcept { return db_path_; }

    /// Check if the database is open and valid.
    [[nodiscard]] bool is_open() const noexcept { return db_ != nullptr; }

private:
    std::string db_path_;
    mutable std::mutex mutex_;

#if TSDUCK_HAS_SQLITE
    sqlite3* db_{nullptr};

    // Prepared statements (cached for performance)
    sqlite3_stmt* stmt_insert_metric_{nullptr};
    sqlite3_stmt* stmt_update_health_success_{nullptr};
    sqlite3_stmt* stmt_update_health_failure_{nullptr};
    sqlite3_stmt* stmt_get_health_{nullptr};
    sqlite3_stmt* stmt_get_all_health_{nullptr};
    sqlite3_stmt* stmt_cleanup_{nullptr};
    sqlite3_stmt* stmt_reset_health_{nullptr};
#endif

    /// Create database tables if they don't exist.
    void create_tables();

    /// Prepare all cached SQL statements.
    void prepare_statements();

    /// Finalize all cached SQL statements.
    void finalize_statements();

    /// Calculate health score based on success/failure ratio and recency.
    /// @param success_count Number of successes.
    /// @param failure_count Number of failures.
    /// @param consecutive_failures Current consecutive failure streak.
    /// @param avg_latency_ms Average latency.
    /// @param last_success_time Unix timestamp of last success.
    /// @return Computed health score (0.0-100.0).
    static double calculate_health_score(int32_t success_count,
                                        int32_t failure_count,
                                        int32_t consecutive_failures,
                                        int64_t avg_latency_ms,
                                        int64_t last_success_time);

    /// Get current Unix timestamp in seconds.
    static int64_t current_timestamp();

    /// Execute a simple SQL statement without parameters.
    bool execute(const char* sql);

    /// Ensure provider_health row exists for the given url_index.
    void ensure_health_row(int32_t url_index, const std::string& url);
};

}  // namespace tsduck_interop::metrics

#endif  // TSDUCK_INTEROP_METRICS_METRICS_DATABASE_HPP
