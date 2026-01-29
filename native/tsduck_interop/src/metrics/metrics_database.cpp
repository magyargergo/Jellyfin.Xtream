// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "metrics_database.hpp"

// This file is only compiled when SQLite is available
#if TSDUCK_HAS_SQLITE

#include <chrono>
#include <cmath>
#include <stdexcept>

#include "sqlite3.h"
#include "../core/logging.hpp"

namespace tsduck_interop::metrics {

namespace {
constexpr const char* kMetricsDb = "MetricsDatabase";

// Default health score for new providers
constexpr double DEFAULT_HEALTH_SCORE = 50.0;

// Health score calculation weights
constexpr double WEIGHT_SUCCESS_RATIO = 40.0;       // Max 40 points from success ratio
constexpr double WEIGHT_CONSECUTIVE_OK = 20.0;      // Max 20 points from no consecutive failures
constexpr double WEIGHT_LATENCY = 20.0;             // Max 20 points from latency (lower is better)
constexpr double WEIGHT_RECENCY = 20.0;             // Max 20 points from recent success

// Latency thresholds for scoring (milliseconds)
constexpr int64_t LATENCY_EXCELLENT = 100;          // <100ms = full points
constexpr int64_t LATENCY_POOR = 2000;              // >2000ms = zero points

// Recency threshold (seconds)
constexpr int64_t RECENCY_FRESH = 60;               // <60s = full points
constexpr int64_t RECENCY_STALE = 3600;             // >1hr = zero points

// SQL statements
constexpr const char* SQL_CREATE_METRICS_TABLE = R"(
CREATE TABLE IF NOT EXISTS provider_metrics (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    url_index INTEGER NOT NULL,
    url TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    quality_score REAL,
    bytes_received INTEGER,
    latency_ms INTEGER,
    failure_type INTEGER,
    error_message TEXT
);
)";

constexpr const char* SQL_CREATE_HEALTH_TABLE = R"(
CREATE TABLE IF NOT EXISTS provider_health (
    url_index INTEGER PRIMARY KEY,
    url TEXT NOT NULL,
    health_score REAL DEFAULT 50.0,
    total_bytes INTEGER DEFAULT 0,
    success_count INTEGER DEFAULT 0,
    failure_count INTEGER DEFAULT 0,
    avg_latency_ms INTEGER DEFAULT 0,
    last_success_time INTEGER,
    last_failure_time INTEGER,
    consecutive_failures INTEGER DEFAULT 0
);
)";

constexpr const char* SQL_CREATE_METRICS_INDEX_URL = R"(
CREATE INDEX IF NOT EXISTS idx_metrics_url ON provider_metrics(url_index);
)";

constexpr const char* SQL_CREATE_METRICS_INDEX_TIME = R"(
CREATE INDEX IF NOT EXISTS idx_metrics_time ON provider_metrics(timestamp);
)";

constexpr const char* SQL_INSERT_METRIC = R"(
INSERT INTO provider_metrics (url_index, url, timestamp, event_type, quality_score, bytes_received, latency_ms, failure_type, error_message)
VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);
)";

constexpr const char* SQL_UPDATE_HEALTH_SUCCESS = R"(
UPDATE provider_health SET
    health_score = ?,
    total_bytes = total_bytes + ?,
    success_count = success_count + 1,
    avg_latency_ms = CASE
        WHEN success_count = 0 THEN ?
        ELSE (avg_latency_ms * success_count + ?) / (success_count + 1)
    END,
    last_success_time = ?,
    consecutive_failures = 0
WHERE url_index = ?;
)";

constexpr const char* SQL_UPDATE_HEALTH_FAILURE = R"(
UPDATE provider_health SET
    health_score = ?,
    failure_count = failure_count + 1,
    last_failure_time = ?,
    consecutive_failures = consecutive_failures + 1
WHERE url_index = ?;
)";

constexpr const char* SQL_GET_HEALTH = R"(
SELECT url_index, health_score, total_bytes, success_count, failure_count,
       avg_latency_ms, last_success_time, last_failure_time, consecutive_failures
FROM provider_health WHERE url_index = ?;
)";

constexpr const char* SQL_GET_ALL_HEALTH = R"(
SELECT url_index, health_score, total_bytes, success_count, failure_count,
       avg_latency_ms, last_success_time, last_failure_time, consecutive_failures
FROM provider_health ORDER BY url_index;
)";

constexpr const char* SQL_CLEANUP_OLD = R"(
DELETE FROM provider_metrics WHERE timestamp < ?;
)";

constexpr const char* SQL_RESET_HEALTH = R"(
UPDATE provider_health SET
    health_score = 50.0,
    total_bytes = 0,
    success_count = 0,
    failure_count = 0,
    avg_latency_ms = 0,
    last_success_time = NULL,
    last_failure_time = NULL,
    consecutive_failures = 0
WHERE url_index = ?;
)";

constexpr const char* SQL_UPSERT_HEALTH = R"(
INSERT INTO provider_health (url_index, url, health_score)
VALUES (?, ?, 50.0)
ON CONFLICT(url_index) DO UPDATE SET url = excluded.url;
)";

constexpr const char* SQL_COUNT_PROVIDERS = R"(
SELECT COUNT(*) FROM provider_health;
)";

}  // namespace

// ============================================================================
// Construction / Destruction
// ============================================================================

MetricsDatabase::MetricsDatabase(const std::string& db_path)
    : db_path_(db_path)
{
    LOG_INFO(kMetricsDb, "Opening database: %s", db_path.c_str());

    int rc = sqlite3_open(db_path.c_str(), &db_);
    if (rc != SQLITE_OK) {
        std::string error = sqlite3_errmsg(db_);
        if (db_) {
            sqlite3_close(db_);
            db_ = nullptr;
        }
        throw std::runtime_error("Failed to open metrics database: " + error);
    }

    // Enable WAL mode for better concurrency
    execute("PRAGMA journal_mode=WAL;");

    // Enable foreign keys
    execute("PRAGMA foreign_keys=ON;");

    // Set busy timeout to avoid SQLITE_BUSY errors
    sqlite3_busy_timeout(db_, 5000);  // 5 seconds

    create_tables();
    prepare_statements();

    LOG_INFO(kMetricsDb, "Database opened successfully");
}

MetricsDatabase::~MetricsDatabase() {
    LOG_DEBUG(kMetricsDb, "Closing database");

    finalize_statements();

    if (db_) {
        sqlite3_close(db_);
        db_ = nullptr;
    }
}

// ============================================================================
// Recording Methods
// ============================================================================

void MetricsDatabase::record_success(int32_t url_index,
                                     const std::string& url,
                                     double quality_score,
                                     int64_t bytes,
                                     int64_t latency_ms)
{
    const std::lock_guard<std::mutex> lock(mutex_);

    if (!db_) {
        return;
    }

    int64_t timestamp = current_timestamp();

    // Ensure health row exists
    ensure_health_row(url_index, url);

    // Insert metric record
    sqlite3_reset(stmt_insert_metric_);
    sqlite3_bind_int(stmt_insert_metric_, 1, url_index);
    sqlite3_bind_text(stmt_insert_metric_, 2, url.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(stmt_insert_metric_, 3, timestamp);
    sqlite3_bind_text(stmt_insert_metric_, 4, "success", -1, SQLITE_STATIC);
    sqlite3_bind_double(stmt_insert_metric_, 5, quality_score);
    sqlite3_bind_int64(stmt_insert_metric_, 6, bytes);
    sqlite3_bind_int64(stmt_insert_metric_, 7, latency_ms);
    sqlite3_bind_null(stmt_insert_metric_, 8);
    sqlite3_bind_null(stmt_insert_metric_, 9);

    int rc = sqlite3_step(stmt_insert_metric_);
    if (rc != SQLITE_DONE) {
        LOG_WARNING(kMetricsDb, "Failed to insert success metric: %s", sqlite3_errmsg(db_));
    }

    // Get current health data to calculate new score
    sqlite3_reset(stmt_get_health_);
    sqlite3_bind_int(stmt_get_health_, 1, url_index);

    int32_t success_count = 0;
    int32_t failure_count = 0;
    int32_t consecutive_failures = 0;
    int64_t avg_latency = latency_ms;

    if (sqlite3_step(stmt_get_health_) == SQLITE_ROW) {
        success_count = sqlite3_column_int(stmt_get_health_, 3);
        failure_count = sqlite3_column_int(stmt_get_health_, 4);
        avg_latency = sqlite3_column_int64(stmt_get_health_, 5);
        consecutive_failures = sqlite3_column_int(stmt_get_health_, 8);
    }

    // Calculate new health score (after this success, consecutive_failures will be 0)
    double new_score = calculate_health_score(
        success_count + 1,  // This success
        failure_count,
        0,                  // Consecutive failures reset to 0
        (avg_latency * success_count + latency_ms) / (success_count + 1),
        timestamp
    );

    // Update health record
    sqlite3_reset(stmt_update_health_success_);
    sqlite3_bind_double(stmt_update_health_success_, 1, new_score);
    sqlite3_bind_int64(stmt_update_health_success_, 2, bytes);
    sqlite3_bind_int64(stmt_update_health_success_, 3, latency_ms);  // For CASE when count=0
    sqlite3_bind_int64(stmt_update_health_success_, 4, latency_ms);  // For averaging
    sqlite3_bind_int64(stmt_update_health_success_, 5, timestamp);
    sqlite3_bind_int(stmt_update_health_success_, 6, url_index);

    rc = sqlite3_step(stmt_update_health_success_);
    if (rc != SQLITE_DONE) {
        LOG_WARNING(kMetricsDb, "Failed to update health on success: %s", sqlite3_errmsg(db_));
    }

    LOG_TRACE(kMetricsDb, "Recorded success: url_index=%d, bytes=%lld, latency=%lld, new_score=%.1f",
              url_index, static_cast<long long>(bytes), static_cast<long long>(latency_ms), new_score);
}

void MetricsDatabase::record_failure(int32_t url_index,
                                     const std::string& url,
                                     FailureType failure_type,
                                     const std::string& error_msg)
{
    const std::lock_guard<std::mutex> lock(mutex_);

    if (!db_) {
        return;
    }

    int64_t timestamp = current_timestamp();

    // Ensure health row exists
    ensure_health_row(url_index, url);

    // Insert metric record
    sqlite3_reset(stmt_insert_metric_);
    sqlite3_bind_int(stmt_insert_metric_, 1, url_index);
    sqlite3_bind_text(stmt_insert_metric_, 2, url.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(stmt_insert_metric_, 3, timestamp);
    sqlite3_bind_text(stmt_insert_metric_, 4, "failure", -1, SQLITE_STATIC);
    sqlite3_bind_null(stmt_insert_metric_, 5);
    sqlite3_bind_null(stmt_insert_metric_, 6);
    sqlite3_bind_null(stmt_insert_metric_, 7);
    sqlite3_bind_int(stmt_insert_metric_, 8, static_cast<int32_t>(failure_type));
    sqlite3_bind_text(stmt_insert_metric_, 9, error_msg.c_str(), -1, SQLITE_TRANSIENT);

    int rc = sqlite3_step(stmt_insert_metric_);
    if (rc != SQLITE_DONE) {
        LOG_WARNING(kMetricsDb, "Failed to insert failure metric: %s", sqlite3_errmsg(db_));
    }

    // Get current health data to calculate new score
    sqlite3_reset(stmt_get_health_);
    sqlite3_bind_int(stmt_get_health_, 1, url_index);

    int32_t success_count = 0;
    int32_t failure_count = 0;
    int32_t consecutive_failures = 0;
    int64_t avg_latency = 0;
    int64_t last_success = 0;

    if (sqlite3_step(stmt_get_health_) == SQLITE_ROW) {
        success_count = sqlite3_column_int(stmt_get_health_, 3);
        failure_count = sqlite3_column_int(stmt_get_health_, 4);
        avg_latency = sqlite3_column_int64(stmt_get_health_, 5);
        if (sqlite3_column_type(stmt_get_health_, 6) != SQLITE_NULL) {
            last_success = sqlite3_column_int64(stmt_get_health_, 6);
        }
        consecutive_failures = sqlite3_column_int(stmt_get_health_, 8);
    }

    // Calculate new health score (after this failure)
    double new_score = calculate_health_score(
        success_count,
        failure_count + 1,      // This failure
        consecutive_failures + 1,  // Increment consecutive failures
        avg_latency,
        last_success
    );

    // Update health record
    sqlite3_reset(stmt_update_health_failure_);
    sqlite3_bind_double(stmt_update_health_failure_, 1, new_score);
    sqlite3_bind_int64(stmt_update_health_failure_, 2, timestamp);
    sqlite3_bind_int(stmt_update_health_failure_, 3, url_index);

    rc = sqlite3_step(stmt_update_health_failure_);
    if (rc != SQLITE_DONE) {
        LOG_WARNING(kMetricsDb, "Failed to update health on failure: %s", sqlite3_errmsg(db_));
    }

    LOG_DEBUG(kMetricsDb, "Recorded failure: url_index=%d, type=%d, new_score=%.1f, error=%s",
              url_index, static_cast<int>(failure_type), new_score, error_msg.c_str());
}

// ============================================================================
// Query Methods
// ============================================================================

bool MetricsDatabase::get_provider_health(int32_t url_index, ProviderHealth* out_health) {
    const std::lock_guard<std::mutex> lock(mutex_);

    if (!db_ || !out_health) {
        return false;
    }

    sqlite3_reset(stmt_get_health_);
    sqlite3_bind_int(stmt_get_health_, 1, url_index);

    if (sqlite3_step(stmt_get_health_) != SQLITE_ROW) {
        return false;
    }

    out_health->url_index = sqlite3_column_int(stmt_get_health_, 0);
    out_health->health_score = sqlite3_column_double(stmt_get_health_, 1);
    out_health->total_bytes = sqlite3_column_int64(stmt_get_health_, 2);
    out_health->success_count = sqlite3_column_int(stmt_get_health_, 3);
    out_health->failure_count = sqlite3_column_int(stmt_get_health_, 4);
    out_health->avg_latency_ms = sqlite3_column_int64(stmt_get_health_, 5);

    if (sqlite3_column_type(stmt_get_health_, 6) != SQLITE_NULL) {
        out_health->last_success_time = sqlite3_column_int64(stmt_get_health_, 6);
    } else {
        out_health->last_success_time = 0;
    }

    if (sqlite3_column_type(stmt_get_health_, 7) != SQLITE_NULL) {
        out_health->last_failure_time = sqlite3_column_int64(stmt_get_health_, 7);
    } else {
        out_health->last_failure_time = 0;
    }

    out_health->consecutive_failures = sqlite3_column_int(stmt_get_health_, 8);
    out_health->reserved = 0;

    return true;
}

std::vector<ProviderHealth> MetricsDatabase::get_all_provider_health() {
    const std::lock_guard<std::mutex> lock(mutex_);

    std::vector<ProviderHealth> results;

    if (!db_) {
        return results;
    }

    sqlite3_reset(stmt_get_all_health_);

    while (sqlite3_step(stmt_get_all_health_) == SQLITE_ROW) {
        ProviderHealth health{};
        health.url_index = sqlite3_column_int(stmt_get_all_health_, 0);
        health.health_score = sqlite3_column_double(stmt_get_all_health_, 1);
        health.total_bytes = sqlite3_column_int64(stmt_get_all_health_, 2);
        health.success_count = sqlite3_column_int(stmt_get_all_health_, 3);
        health.failure_count = sqlite3_column_int(stmt_get_all_health_, 4);
        health.avg_latency_ms = sqlite3_column_int64(stmt_get_all_health_, 5);

        if (sqlite3_column_type(stmt_get_all_health_, 6) != SQLITE_NULL) {
            health.last_success_time = sqlite3_column_int64(stmt_get_all_health_, 6);
        }

        if (sqlite3_column_type(stmt_get_all_health_, 7) != SQLITE_NULL) {
            health.last_failure_time = sqlite3_column_int64(stmt_get_all_health_, 7);
        }

        health.consecutive_failures = sqlite3_column_int(stmt_get_all_health_, 8);
        health.reserved = 0;

        results.push_back(health);
    }

    return results;
}

int32_t MetricsDatabase::get_provider_count() {
    const std::lock_guard<std::mutex> lock(mutex_);

    if (!db_) {
        return 0;
    }

    sqlite3_stmt* stmt = nullptr;
    int rc = sqlite3_prepare_v2(db_, SQL_COUNT_PROVIDERS, -1, &stmt, nullptr);
    if (rc != SQLITE_OK) {
        return 0;
    }

    int32_t count = 0;
    if (sqlite3_step(stmt) == SQLITE_ROW) {
        count = sqlite3_column_int(stmt, 0);
    }

    sqlite3_finalize(stmt);
    return count;
}

// ============================================================================
// Housekeeping Methods
// ============================================================================

void MetricsDatabase::cleanup_old_records(int64_t max_age_seconds) {
    const std::lock_guard<std::mutex> lock(mutex_);

    if (!db_) {
        return;
    }

    int64_t cutoff = current_timestamp() - max_age_seconds;

    sqlite3_reset(stmt_cleanup_);
    sqlite3_bind_int64(stmt_cleanup_, 1, cutoff);

    int rc = sqlite3_step(stmt_cleanup_);
    if (rc != SQLITE_DONE) {
        LOG_WARNING(kMetricsDb, "Failed to cleanup old records: %s", sqlite3_errmsg(db_));
    } else {
        int deleted = sqlite3_changes(db_);
        if (deleted > 0) {
            LOG_DEBUG(kMetricsDb, "Cleaned up %d old metric records", deleted);
        }
    }
}

void MetricsDatabase::reset_all_health_scores() {
    const std::lock_guard<std::mutex> lock(mutex_);

    if (!db_) {
        return;
    }

    bool ok = execute(R"(
        UPDATE provider_health SET
            health_score = 50.0,
            total_bytes = 0,
            success_count = 0,
            failure_count = 0,
            avg_latency_ms = 0,
            last_success_time = NULL,
            last_failure_time = NULL,
            consecutive_failures = 0;
    )");

    if (ok) {
        LOG_INFO(kMetricsDb, "Reset all provider health scores");
    }
}

void MetricsDatabase::reset_provider_health(int32_t url_index) {
    const std::lock_guard<std::mutex> lock(mutex_);

    if (!db_) {
        return;
    }

    sqlite3_reset(stmt_reset_health_);
    sqlite3_bind_int(stmt_reset_health_, 1, url_index);

    int rc = sqlite3_step(stmt_reset_health_);
    if (rc != SQLITE_DONE) {
        LOG_WARNING(kMetricsDb, "Failed to reset health for url_index %d: %s",
                    url_index, sqlite3_errmsg(db_));
    }
}

// ============================================================================
// Private Implementation
// ============================================================================

void MetricsDatabase::create_tables() {
    execute(SQL_CREATE_METRICS_TABLE);
    execute(SQL_CREATE_HEALTH_TABLE);
    execute(SQL_CREATE_METRICS_INDEX_URL);
    execute(SQL_CREATE_METRICS_INDEX_TIME);
}

void MetricsDatabase::prepare_statements() {
    auto prepare = [this](const char* sql, sqlite3_stmt** stmt) {
        int rc = sqlite3_prepare_v2(db_, sql, -1, stmt, nullptr);
        if (rc != SQLITE_OK) {
            LOG_ERROR(kMetricsDb, "Failed to prepare statement: %s", sqlite3_errmsg(db_));
        }
    };

    prepare(SQL_INSERT_METRIC, &stmt_insert_metric_);
    prepare(SQL_UPDATE_HEALTH_SUCCESS, &stmt_update_health_success_);
    prepare(SQL_UPDATE_HEALTH_FAILURE, &stmt_update_health_failure_);
    prepare(SQL_GET_HEALTH, &stmt_get_health_);
    prepare(SQL_GET_ALL_HEALTH, &stmt_get_all_health_);
    prepare(SQL_CLEANUP_OLD, &stmt_cleanup_);
    prepare(SQL_RESET_HEALTH, &stmt_reset_health_);
}

void MetricsDatabase::finalize_statements() {
    auto finalize = [](sqlite3_stmt*& stmt) {
        if (stmt) {
            sqlite3_finalize(stmt);
            stmt = nullptr;
        }
    };

    finalize(stmt_insert_metric_);
    finalize(stmt_update_health_success_);
    finalize(stmt_update_health_failure_);
    finalize(stmt_get_health_);
    finalize(stmt_get_all_health_);
    finalize(stmt_cleanup_);
    finalize(stmt_reset_health_);
}

double MetricsDatabase::calculate_health_score(int32_t success_count,
                                               int32_t failure_count,
                                               int32_t consecutive_failures,
                                               int64_t avg_latency_ms,
                                               int64_t last_success_time)
{
    double score = 0.0;

    // 1. Success ratio component (0-40 points)
    int32_t total = success_count + failure_count;
    if (total > 0) {
        double ratio = static_cast<double>(success_count) / static_cast<double>(total);
        score += ratio * WEIGHT_SUCCESS_RATIO;
    } else {
        // No data yet - give neutral score
        score += WEIGHT_SUCCESS_RATIO / 2.0;
    }

    // 2. Consecutive failures penalty (0-20 points)
    // Each consecutive failure reduces this component
    if (consecutive_failures == 0) {
        score += WEIGHT_CONSECUTIVE_OK;
    } else if (consecutive_failures < 5) {
        score += WEIGHT_CONSECUTIVE_OK * (1.0 - consecutive_failures * 0.2);
    }
    // 5+ consecutive failures = 0 points from this component

    // 3. Latency component (0-20 points)
    if (success_count > 0 && avg_latency_ms > 0) {
        if (avg_latency_ms <= LATENCY_EXCELLENT) {
            score += WEIGHT_LATENCY;
        } else if (avg_latency_ms >= LATENCY_POOR) {
            score += 0.0;
        } else {
            // Linear interpolation between excellent and poor
            double latency_ratio = static_cast<double>(LATENCY_POOR - avg_latency_ms) /
                                   static_cast<double>(LATENCY_POOR - LATENCY_EXCELLENT);
            score += latency_ratio * WEIGHT_LATENCY;
        }
    } else {
        // No latency data - give neutral score
        score += WEIGHT_LATENCY / 2.0;
    }

    // 4. Recency component (0-20 points)
    if (last_success_time > 0) {
        int64_t age_seconds = current_timestamp() - last_success_time;
        if (age_seconds <= RECENCY_FRESH) {
            score += WEIGHT_RECENCY;
        } else if (age_seconds >= RECENCY_STALE) {
            score += 0.0;
        } else {
            // Linear interpolation
            double recency_ratio = static_cast<double>(RECENCY_STALE - age_seconds) /
                                   static_cast<double>(RECENCY_STALE - RECENCY_FRESH);
            score += recency_ratio * WEIGHT_RECENCY;
        }
    } else {
        // No success yet - give neutral score
        score += WEIGHT_RECENCY / 2.0;
    }

    // Clamp to valid range
    return std::clamp(score, 0.0, 100.0);
}

int64_t MetricsDatabase::current_timestamp() {
    auto now = std::chrono::system_clock::now();
    auto duration = now.time_since_epoch();
    return std::chrono::duration_cast<std::chrono::seconds>(duration).count();
}

bool MetricsDatabase::execute(const char* sql) {
    char* errmsg = nullptr;
    int rc = sqlite3_exec(db_, sql, nullptr, nullptr, &errmsg);
    if (rc != SQLITE_OK) {
        LOG_ERROR(kMetricsDb, "SQL error: %s", errmsg ? errmsg : "unknown");
        sqlite3_free(errmsg);
        return false;
    }
    return true;
}

void MetricsDatabase::ensure_health_row(int32_t url_index, const std::string& url) {
    sqlite3_stmt* stmt = nullptr;
    int rc = sqlite3_prepare_v2(db_, SQL_UPSERT_HEALTH, -1, &stmt, nullptr);
    if (rc != SQLITE_OK) {
        LOG_WARNING(kMetricsDb, "Failed to prepare upsert: %s", sqlite3_errmsg(db_));
        return;
    }

    sqlite3_bind_int(stmt, 1, url_index);
    sqlite3_bind_text(stmt, 2, url.c_str(), -1, SQLITE_TRANSIENT);

    sqlite3_step(stmt);
    sqlite3_finalize(stmt);
}

}  // namespace tsduck_interop::metrics

#endif  // TSDUCK_HAS_SQLITE
