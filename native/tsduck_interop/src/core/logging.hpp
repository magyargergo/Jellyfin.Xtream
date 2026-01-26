// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CORE_LOGGING_HPP
#define TSDUCK_INTEROP_CORE_LOGGING_HPP

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>

namespace tsduck_interop {
namespace logging {

// ============================================================================
// Log Levels
// ============================================================================

enum class LogLevel : int32_t {
    None = 0,     // No logging
    Error = 1,    // Errors only
    Warning = 2,  // Warnings and errors
    Info = 3,     // Info, warnings, and errors
    Debug = 4,    // All messages including debug
    Trace = 5     // Most verbose - includes detailed tracing
};

// ============================================================================
// Log Callback Type
// ============================================================================

/// Callback signature for custom log handlers.
/// @param level Log level (LogLevel enum value)
/// @param component Component name (e.g., "StreamPipeline", "Analyzer")
/// @param message Formatted log message
/// @param user_data User-provided context pointer
using LogCallback = void (*)(int32_t level, const char* component, const char* message, void* user_data);

// ============================================================================
// Global Logger State
// ============================================================================

namespace detail {

// Global state - using atomics for thread safety
inline std::atomic<LogLevel> g_log_level{LogLevel::Warning};
inline std::atomic<LogCallback> g_log_callback{nullptr};
inline std::atomic<void*> g_log_user_data{nullptr};
inline std::atomic<FILE*> g_log_file{stderr};

/// Get log level name
inline const char* level_name(LogLevel level) noexcept {
    switch (level) {
        case LogLevel::Error:
            return "ERROR";
        case LogLevel::Warning:
            return "WARN";
        case LogLevel::Info:
            return "INFO";
        case LogLevel::Debug:
            return "DEBUG";
        case LogLevel::Trace:
            return "TRACE";
        default:
            return "UNKNOWN";
    }
}

/// Check if logging is enabled for the given level
inline bool is_enabled(LogLevel level) noexcept {
    return static_cast<int32_t>(level) <= static_cast<int32_t>(g_log_level.load(std::memory_order_relaxed));
}

/// Internal log implementation
inline void log_impl(LogLevel level, const char* component, const char* format, va_list args) noexcept {
    if (!is_enabled(level)) {
        return;
    }

    // Format the message
    // flawfinder: ignore - format string is controlled by our LOG_* macros (compile-time constants)
    char message[1024];  // flawfinder: ignore - fixed size is intentional for stack allocation
    vsnprintf(message, sizeof(message), format, args);

    // Try custom callback first
    LogCallback callback = g_log_callback.load(std::memory_order_acquire);
    if (callback != nullptr) {
        void* user_data = g_log_user_data.load(std::memory_order_relaxed);
        callback(static_cast<int32_t>(level), component, message, user_data);
        return;
    }

    // Fall back to file output
    FILE* file = g_log_file.load(std::memory_order_relaxed);
    if (file != nullptr) {
        fprintf(file, "[NATIVE][%s][%s] %s\n", level_name(level), component, message);
        fflush(file);
    }
}

}  // namespace detail

// ============================================================================
// Configuration Functions
// ============================================================================

/// Set the global log level
inline void set_level(LogLevel level) noexcept {
    detail::g_log_level.store(level, std::memory_order_release);
}

/// Get the current log level
inline LogLevel get_level() noexcept {
    return detail::g_log_level.load(std::memory_order_acquire);
}

/// Set custom log callback (set to nullptr to use default file output)
inline void set_callback(LogCallback callback, void* user_data = nullptr) noexcept {
    detail::g_log_user_data.store(user_data, std::memory_order_relaxed);
    detail::g_log_callback.store(callback, std::memory_order_release);
}

/// Set log output file (default: stderr)
inline void set_file(FILE* file) noexcept {
    detail::g_log_file.store(file, std::memory_order_release);
}

/// Check if a log level is enabled
inline bool is_enabled(LogLevel level) noexcept {
    return detail::is_enabled(level);
}

// ============================================================================
// Logging Functions
// ============================================================================

/// Log an error message
inline void error(const char* component, const char* format, ...) noexcept {
    if (!detail::is_enabled(LogLevel::Error)) {
        return;
    }
    va_list args;
    va_start(args, format);
    detail::log_impl(LogLevel::Error, component, format, args);
    va_end(args);
}

/// Log a warning message
inline void warning(const char* component, const char* format, ...) noexcept {
    if (!detail::is_enabled(LogLevel::Warning)) {
        return;
    }
    va_list args;
    va_start(args, format);
    detail::log_impl(LogLevel::Warning, component, format, args);
    va_end(args);
}

/// Log an info message
inline void info(const char* component, const char* format, ...) noexcept {
    if (!detail::is_enabled(LogLevel::Info)) {
        return;
    }
    va_list args;
    va_start(args, format);
    detail::log_impl(LogLevel::Info, component, format, args);
    va_end(args);
}

/// Log a debug message
inline void debug(const char* component, const char* format, ...) noexcept {
    if (!detail::is_enabled(LogLevel::Debug)) {
        return;
    }
    va_list args;
    va_start(args, format);
    detail::log_impl(LogLevel::Debug, component, format, args);
    va_end(args);
}

/// Log a trace message
inline void trace(const char* component, const char* format, ...) noexcept {
    if (!detail::is_enabled(LogLevel::Trace)) {
        return;
    }
    va_list args;
    va_start(args, format);
    detail::log_impl(LogLevel::Trace, component, format, args);
    va_end(args);
}

}  // namespace logging

// ============================================================================
// Convenience Macros
// ============================================================================

// Component-scoped logging macros
// Usage: LOG_ERROR("MyComponent", "Error code: %d", errorCode);

#define LOG_ERROR(component, ...) ::tsduck_interop::logging::error(component, __VA_ARGS__)
#define LOG_WARNING(component, ...) ::tsduck_interop::logging::warning(component, __VA_ARGS__)
#define LOG_INFO(component, ...) ::tsduck_interop::logging::info(component, __VA_ARGS__)
#define LOG_DEBUG(component, ...) ::tsduck_interop::logging::debug(component, __VA_ARGS__)
#define LOG_TRACE(component, ...) ::tsduck_interop::logging::trace(component, __VA_ARGS__)

// Check if level is enabled (for conditional expensive logging)
#define LOG_IS_ENABLED(level) ::tsduck_interop::logging::is_enabled(::tsduck_interop::logging::LogLevel::level)

}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CORE_LOGGING_HPP
