// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_SCTE35_MONITOR_HPP
#define TSDUCK_INTEROP_ANALYSIS_SCTE35_MONITOR_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <tsduck.h>

#include "../concurrency/seqlock.hpp"
#include "../core/constants.hpp"
#include "../core/types.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

// ============================================================================
// SCTE-35 Constants
// ============================================================================

/// CUEI (SCTE-35) registration descriptor format identifier
inline constexpr std::uint32_t SCTE35_FORMAT_ID = 0x43554549;  // "CUEI"

/// SCTE-35 stream type in PMT (ATSC A/53 style)
inline constexpr std::uint8_t SCTE35_STREAM_TYPE = 0x86;

/// SCTE-35 table ID for splice_info_section
inline constexpr std::uint8_t SCTE35_TID = 0xFC;

/// SCTE-35 splice command types
namespace SpliceCommand {
    inline constexpr std::uint8_t SPLICE_NULL = 0x00;
    inline constexpr std::uint8_t SPLICE_SCHEDULE = 0x04;
    inline constexpr std::uint8_t SPLICE_INSERT = 0x05;
    inline constexpr std::uint8_t TIME_SIGNAL = 0x06;
    inline constexpr std::uint8_t BANDWIDTH_RESERVATION = 0x07;
    inline constexpr std::uint8_t PRIVATE_COMMAND = 0xFF;
}

// ============================================================================
// SCTE-35 Splice State
// ============================================================================

/// Current splice state for tracking ad breaks
enum class SpliceState : std::uint8_t {
    InContent = 0,      ///< Normal content playback
    InBreak = 1,        ///< Inside ad break (out_of_network=true)
    Transitioning = 2   ///< Transitioning between states
};

// ============================================================================
// SCTE-35 Monitor - Splice Information Section Parser
//
// Parses SCTE-35 splice_info_section directly from raw bytes.
// Uses TsDuck's SectionDemux for section assembly only.
//
// Thread safety:
// - Single writer (packet processing thread)
// - Multiple readers (metrics/callback threads via seqlock)
// ============================================================================

class Scte35Monitor : public ts::SectionHandlerInterface {
public:
    // ========================================================================
    // Public State
    // ========================================================================

    /// Currently detected SCTE-35 PIDs (from PMT analysis)
    std::array<std::uint16_t, MAX_PROGRAMS> scte35_pids{};
    std::atomic<std::int32_t> scte35_pid_count{0};

    /// Current splice state
    std::atomic<SpliceState> current_state{SpliceState::InContent};

    /// Total events received
    std::atomic<std::int64_t> total_events{0};

    /// Event ring buffer with seqlock protection
    concurrency::Seqlock events_seqlock;
    std::array<Scte35Event, SCTE35_EVENT_BUFFER_SIZE> events{};
    std::atomic<std::size_t> event_write_idx{0};
    std::atomic<std::size_t> event_count{0};

    /// Most recent event for quick access
    concurrency::Seqlock current_event_seqlock;
    Scte35Event current_event{};
    std::atomic<bool> has_current_event{false};

    // ========================================================================
    // Constructor
    // ========================================================================

    explicit Scte35Monitor(ts::DuckContext& duck)
        : duck_(duck), demux_(duck, nullptr, this) {
    }

    // ========================================================================
    // PID Management
    // ========================================================================

    /// Register a SCTE-35 PID discovered from PMT.
    void add_scte35_pid(std::uint16_t pid) noexcept {
        if (pid >= ts::PID_NULL) {
            return;
        }

        // Check if already registered
        std::int32_t count = scte35_pid_count.load(std::memory_order_relaxed);
        for (std::int32_t i = 0; i < count; ++i) {
            if (scte35_pids[i] == pid) {
                return;
            }
        }

        // Add new PID
        if (count < static_cast<std::int32_t>(MAX_PROGRAMS)) {
            scte35_pids[count] = pid;
            scte35_pid_count.store(count + 1, std::memory_order_release);
            demux_.addPID(pid);
        }
    }

    /// Check if a PID is a registered SCTE-35 PID.
    [[nodiscard]] bool is_scte35_pid(std::uint16_t pid) const noexcept {
        std::int32_t count = scte35_pid_count.load(std::memory_order_relaxed);
        for (std::int32_t i = 0; i < count; ++i) {
            if (scte35_pids[i] == pid) {
                return true;
            }
        }
        return false;
    }

    /// Detect SCTE-35 PID from PMT stream entry.
    /// Primarily uses stream_type 0x86 which is the standard SCTE-35 identifier.
    /// @param stream_type The PMT stream type
    /// @param pid The elementary stream PID
    /// @return true if this is a SCTE-35 stream
    [[nodiscard]] bool detect_scte35_from_pmt(std::uint8_t stream_type,
                                              std::uint16_t pid) noexcept {
        if (stream_type == SCTE35_STREAM_TYPE) {
            add_scte35_pid(pid);
            return true;
        }
        return false;
    }

    // ========================================================================
    // Packet Processing
    // ========================================================================

    /// Feed a TS packet for SCTE-35 processing.
    void feed_packet(ts::TSPacket& pkt, std::int64_t packet_idx) noexcept {
        std::uint16_t pid = pkt.getPID();
        if (!is_scte35_pid(pid)) {
            return;
        }

        current_packet_idx_ = packet_idx;
        current_pid_ = pid;
        demux_.feedPacket(pkt);
    }

    // ========================================================================
    // Event Retrieval
    // ========================================================================

    [[nodiscard]] std::int32_t get_events(Scte35EventNative* out,
                                          std::int32_t max_events) const noexcept {
        if (out == nullptr || max_events <= 0) {
            return 0;
        }

        std::uint64_t seq;
        std::int32_t count;

        do {
            seq = events_seqlock.begin_read();
            std::size_t available = event_count.load(std::memory_order_acquire);
            count = static_cast<std::int32_t>(
                std::min(static_cast<std::size_t>(max_events), available));

            std::size_t write_idx = event_write_idx.load(std::memory_order_acquire);

            for (std::int32_t i = 0; i < count; ++i) {
                std::size_t src_idx = (write_idx - count + i) & (SCTE35_EVENT_BUFFER_SIZE - 1);
                const auto& src = events[src_idx];
                auto& dst = out[i];

                dst.splice_event_id = src.splice_event_id;
                dst.pts_time = src.pts_time;
                dst.duration_pts = src.duration_pts;
                dst.out_of_network = src.out_of_network ? 1 : 0;
                dst.splice_immediate = src.splice_immediate ? 1 : 0;
                dst.splice_command_type = src.splice_command_type;
                dst.scte35_pid = src.scte35_pid;
                dst.packet_index = src.packet_index;
            }
        } while (!events_seqlock.read_consistent(seq));

        return count;
    }

    [[nodiscard]] bool get_current_event(Scte35EventNative* out) const noexcept {
        if (out == nullptr || !has_current_event.load(std::memory_order_acquire)) {
            return false;
        }

        Scte35Event evt = concurrency::seqlock_read(current_event_seqlock, current_event);

        out->splice_event_id = evt.splice_event_id;
        out->pts_time = evt.pts_time;
        out->duration_pts = evt.duration_pts;
        out->out_of_network = evt.out_of_network ? 1 : 0;
        out->splice_immediate = evt.splice_immediate ? 1 : 0;
        out->splice_command_type = evt.splice_command_type;
        out->scte35_pid = evt.scte35_pid;
        out->packet_index = evt.packet_index;

        return true;
    }

    [[nodiscard]] SpliceState get_splice_state() const noexcept {
        return current_state.load(std::memory_order_acquire);
    }

    [[nodiscard]] bool is_in_ad_break() const noexcept {
        return current_state.load(std::memory_order_acquire) == SpliceState::InBreak;
    }

    [[nodiscard]] std::int64_t get_event_count() const noexcept {
        return total_events.load(std::memory_order_acquire);
    }

    // ========================================================================
    // Reset
    // ========================================================================

    void reset() noexcept {
        demux_.reset();

        std::int32_t count = scte35_pid_count.load(std::memory_order_relaxed);
        for (std::int32_t i = 0; i < count; ++i) {
            demux_.addPID(scte35_pids[i]);
        }

        current_state.store(SpliceState::InContent, std::memory_order_release);
        total_events.store(0, std::memory_order_release);
        event_write_idx.store(0, std::memory_order_release);
        event_count.store(0, std::memory_order_release);
        has_current_event.store(false, std::memory_order_release);

        auto seq = events_seqlock.begin_write();
        events.fill(Scte35Event{});
        events_seqlock.end_write(seq);

        auto curr_seq = current_event_seqlock.begin_write();
        current_event = Scte35Event{};
        current_event_seqlock.end_write(curr_seq);
    }

    void reset_full() noexcept {
        demux_.reset();
        scte35_pids.fill(0);
        scte35_pid_count.store(0, std::memory_order_release);
        reset();
    }

    // ========================================================================
    // ts::SectionHandlerInterface Implementation
    // ========================================================================

    void handleSection(ts::SectionDemux& /*demux*/,
                       const ts::Section& section) override {
        if (section.tableId() != SCTE35_TID) {
            return;
        }

        // Validate section structure
        if (!section.isValid()) {
            return;
        }

        // Validate CRC-32/MPEG-2 for SCTE-35 sections
        // Note: SCTE-35 uses section_syntax_indicator=0, but still has CRC
        // The CRC is always the last 4 bytes of the section
        const std::uint8_t* data = section.content();
        std::size_t size = section.size();
        if (size < 4) {
            return;  // Too short to have CRC
        }

        // Compute CRC over all bytes except the last 4 (the stored CRC)
        ts::CRC32 crc;
        crc.add(data, size - 4);

        // Extract stored CRC (big-endian in the section)
        std::uint32_t stored_crc = (static_cast<std::uint32_t>(data[size - 4]) << 24) |
                                   (static_cast<std::uint32_t>(data[size - 3]) << 16) |
                                   (static_cast<std::uint32_t>(data[size - 2]) << 8) |
                                   static_cast<std::uint32_t>(data[size - 1]);

        if (crc.value() != stored_crc) {
            return;  // CRC mismatch
        }

        parse_splice_info_section(section);
    }

private:
    ts::DuckContext& duck_;
    ts::SectionDemux demux_;
    std::int64_t current_packet_idx_{0};
    std::uint16_t current_pid_{0};

    // ========================================================================
    // SCTE-35 Section Parsing (Raw Bytes)
    //
    // splice_info_section() {
    //   table_id                      8 bits  = 0xFC
    //   section_syntax_indicator      1 bit   = 0
    //   private_indicator             1 bit
    //   reserved                      2 bits
    //   section_length               12 bits
    //   protocol_version              8 bits
    //   encrypted_packet              1 bit
    //   encryption_algorithm          6 bits
    //   pts_adjustment               33 bits
    //   cw_index                      8 bits
    //   tier                         12 bits
    //   splice_command_length        12 bits
    //   splice_command_type           8 bits
    //   splice_command()              variable
    //   ...
    // }
    // ========================================================================

    void parse_splice_info_section(const ts::Section& section) noexcept {
        const std::uint8_t* data = section.payload();
        std::size_t size = section.payloadSize();

        // Minimum size: protocol_version(1) + flags(1) + pts_adj(4) + cw_index(1) +
        //               tier+cmd_len(3) + cmd_type(1) = 11 bytes
        if (size < 11) {
            return;
        }

        // Parse header
        // std::uint8_t protocol_version = data[0];
        bool encrypted = (data[1] & 0x80) != 0;
        if (encrypted) {
            return;  // Can't parse encrypted sections
        }

        // PTS adjustment (33 bits, but stored in 5 bytes starting at offset 1, bit 0 is MSB)
        // Actually at bytes 1-5: encrypted(1) + algo(6) + pts_adj(33)
        // uint64_t pts_adjustment = ...;  // Not used for basic parsing

        // Skip to splice_command_length and type
        // Byte 6: cw_index (8 bits)
        // Byte 7: tier[11:4]
        // Byte 8: tier[3:0] + splice_cmd_len[11:8]
        // Byte 9: splice_cmd_len[7:0]
        // Byte 10: splice_command_type
        std::size_t offset = 6;
        if (offset + 5 > size) return;  // Need at least through byte 10

        // std::uint8_t cw_index = data[offset];  // data[6]
        // std::uint16_t tier = (static_cast<uint16_t>(data[7]) << 4) | (data[8] >> 4);
        std::uint16_t splice_cmd_len = ((data[8] & 0x0F) << 8) | data[9];
        (void)splice_cmd_len;  // May be 0xFFF for unspecified

        offset = 10;  // Start of splice_command_type
        if (offset >= size) return;

        std::uint8_t cmd_type = data[offset];
        offset++;

        auto now_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();

        Scte35Event evt{};
        evt.scte35_pid = current_pid_;
        evt.packet_index = current_packet_idx_;
        evt.timestamp_ns = now_ns;
        evt.splice_command_type = cmd_type;

        switch (cmd_type) {
            case SpliceCommand::SPLICE_INSERT:
                parse_splice_insert_cmd(data + offset, size - offset, evt);
                record_event(evt);
                return;

            case SpliceCommand::TIME_SIGNAL:
                parse_time_signal_cmd(data + offset, size - offset, evt);
                record_event(evt);
                return;

            case SpliceCommand::SPLICE_NULL:
                // Heartbeat - don't record
                return;

            default:
                // Record unknown commands
                record_event(evt);
                return;
        }
    }

    // splice_insert() {
    //   splice_event_id              32 bits
    //   splice_event_cancel_indicator 1 bit
    //   reserved                      7 bits
    //   if (!splice_event_cancel_indicator) {
    //     out_of_network_indicator    1 bit
    //     program_splice_flag         1 bit
    //     duration_flag               1 bit
    //     splice_immediate_flag       1 bit
    //     reserved                    4 bits
    //     if (program_splice_flag && !splice_immediate_flag) {
    //       splice_time()
    //     }
    //     if (duration_flag) {
    //       break_duration()
    //     }
    //     ...
    //   }
    // }
    void parse_splice_insert_cmd(const std::uint8_t* data, std::size_t size,
                                 Scte35Event& evt) noexcept {
        if (size < 5) return;

        evt.splice_event_id = (static_cast<std::uint32_t>(data[0]) << 24) |
                              (static_cast<std::uint32_t>(data[1]) << 16) |
                              (static_cast<std::uint32_t>(data[2]) << 8) |
                              static_cast<std::uint32_t>(data[3]);

        bool cancel = (data[4] & 0x80) != 0;
        if (cancel) {
            current_state.store(SpliceState::InContent, std::memory_order_release);
            return;
        }

        if (size < 6) return;

        bool out_of_network = (data[5] & 0x80) != 0;
        bool program_splice = (data[5] & 0x40) != 0;
        bool duration_flag = (data[5] & 0x20) != 0;
        bool splice_immediate = (data[5] & 0x10) != 0;

        evt.out_of_network = out_of_network;
        evt.splice_immediate = splice_immediate;

        std::size_t offset = 6;

        // Parse splice_time if present
        if (program_splice && !splice_immediate) {
            if (offset < size) {
                bool time_specified = (data[offset] & 0x80) != 0;
                if (time_specified && offset + 5 <= size) {
                    evt.pts_time = (static_cast<std::uint64_t>(data[offset] & 0x01) << 32) |
                                   (static_cast<std::uint64_t>(data[offset + 1]) << 24) |
                                   (static_cast<std::uint64_t>(data[offset + 2]) << 16) |
                                   (static_cast<std::uint64_t>(data[offset + 3]) << 8) |
                                   static_cast<std::uint64_t>(data[offset + 4]);
                    offset += 5;
                } else {
                    offset += 1;  // time_specified = false, just 1 byte
                }
            }
        }

        // Parse break_duration if present
        // break_duration() {
        //   auto_return            1 bit
        //   reserved               6 bits
        //   duration              33 bits
        // }
        if (duration_flag && offset + 5 <= size) {
            evt.duration_pts = (static_cast<std::uint64_t>(data[offset] & 0x01) << 32) |
                               (static_cast<std::uint64_t>(data[offset + 1]) << 24) |
                               (static_cast<std::uint64_t>(data[offset + 2]) << 16) |
                               (static_cast<std::uint64_t>(data[offset + 3]) << 8) |
                               static_cast<std::uint64_t>(data[offset + 4]);
        }

        // Update splice state
        if (out_of_network) {
            current_state.store(SpliceState::InBreak, std::memory_order_release);
        } else {
            current_state.store(SpliceState::InContent, std::memory_order_release);
        }
    }

    // time_signal() {
    //   splice_time()
    // }
    void parse_time_signal_cmd(const std::uint8_t* data, std::size_t size,
                               Scte35Event& evt) noexcept {
        if (size < 1) return;

        bool time_specified = (data[0] & 0x80) != 0;
        if (time_specified && size >= 5) {
            evt.pts_time = (static_cast<std::uint64_t>(data[0] & 0x01) << 32) |
                           (static_cast<std::uint64_t>(data[1]) << 24) |
                           (static_cast<std::uint64_t>(data[2]) << 16) |
                           (static_cast<std::uint64_t>(data[3]) << 8) |
                           static_cast<std::uint64_t>(data[4]);
        }

        evt.out_of_network = false;
        evt.splice_immediate = false;
    }

    void record_event(const Scte35Event& evt) noexcept {
        total_events.fetch_add(1, std::memory_order_relaxed);

        // Update current event
        {
            auto seq = current_event_seqlock.begin_write();
            current_event = evt;
            current_event_seqlock.end_write(seq);
            has_current_event.store(true, std::memory_order_release);
        }

        // Add to ring buffer
        {
            auto seq = events_seqlock.begin_write();
            std::size_t idx = event_write_idx.load(std::memory_order_relaxed);
            events[idx & (SCTE35_EVENT_BUFFER_SIZE - 1)] = evt;
            event_write_idx.store(idx + 1, std::memory_order_release);

            std::size_t count = event_count.load(std::memory_order_relaxed);
            if (count < SCTE35_EVENT_BUFFER_SIZE) {
                event_count.store(count + 1, std::memory_order_release);
            }
            events_seqlock.end_write(seq);
        }
    }
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_SCTE35_MONITOR_HPP
