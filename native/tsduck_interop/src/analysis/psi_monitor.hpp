// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_PSI_MONITOR_HPP
#define TSDUCK_INTEROP_ANALYSIS_PSI_MONITOR_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <tsduck.h>
#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

// ============================================================================
// Stream Type Classification (ISO 13818-1 Table 2-36 + common extensions)
// ============================================================================

inline bool classify_stream_type(uint8_t stream_type, bool& is_video, bool& is_audio) noexcept {
    is_video = false;
    is_audio = false;

    switch (stream_type) {
        // Video codecs
        case 0x01:  // MPEG-1 Video
        case 0x02:  // MPEG-2 Video
        case 0x10:  // MPEG-4 Visual
        case 0x1B:  // H.264/AVC
        case 0x24:  // H.265/HEVC
        case 0x33:  // H.266/VVC
            is_video = true;
            return true;

        // Audio codecs
        case 0x03:  // MPEG-1 Audio
        case 0x04:  // MPEG-2 Audio
        case 0x0F:  // AAC (ADTS)
        case 0x11:  // AAC LATM
        case 0x81:  // AC-3 (ATSC)
        case 0x82:  // DTS
        case 0x83:  // DTS-HD (Lossless)
        case 0x84:  // AC-3 (alternate)
        case 0x85:  // DTS-HD (Core+Extension)
        case 0x86:  // DTS-HD (High Resolution)
        case 0x87:  // E-AC-3
            is_audio = true;
            return true;

        default:
            return false;
    }
}

// ============================================================================
// Program and Elementary Stream Structures
// ============================================================================

struct ProgramEntry {
    uint16_t program_number{0};
    uint16_t pmt_pid{0};
    uint16_t pcr_pid{0x1FFF};
    int64_t last_pmt_packet_idx{-1};
    bool pmt_received{false};
    bool active{false};

    // Elementary streams for this program
    struct EsEntry {
        uint16_t pid{0};
        uint8_t stream_type{0};
        bool is_video{false};
        bool is_audio{false};
        bool active{false};
    };
    std::array<EsEntry, MAX_ELEMENTARY_STREAMS> streams{};
    int32_t stream_count{0};
};

// ============================================================================
// PSI Monitor — PAT/PMT Parser using TsDuck's SectionDemux
//
// Uses ts::SectionDemux for section accumulation, CRC validation, and
// version filtering. Implements ts::TableHandlerInterface to receive
// complete, validated PAT/PMT tables which are then deserialized via
// ts::PAT and ts::PMT classes.
// ============================================================================

class PsiMonitor : public ts::TableHandlerInterface {
public:
    // Program table (populated from PAT + PMT)
    std::array<ProgramEntry, MAX_PROGRAMS> programs{};
    std::atomic<int32_t> program_count{0};

    // Timing — packet index of last PAT reception
    std::atomic<int64_t> last_pat_packet_idx{-1};

    // CRC error counter (for TR 101 290 — tracks sections discarded by SectionDemux)
    std::atomic<int64_t> crc_errors{0};

    // Seqlock for compound reads
    concurrency::Seqlock seqlock;

    explicit PsiMonitor(ts::DuckContext& duck) : duck_(duck), demux_(duck, this) {
        // Subscribe to PAT (PID 0x0000)
        demux_.addPID(ts::PID_PAT);
    }

    /// Check if a PID is a known PMT PID
    bool is_pmt_pid(uint16_t pid) const noexcept {
        int32_t count = program_count.load(std::memory_order_relaxed);
        for (int32_t i = 0; i < count; i++) {
            if (programs[i].active && programs[i].pmt_pid == pid) {
                return true;
            }
        }
        return false;
    }

    /// Get program count
    int32_t get_program_count() const noexcept { return program_count.load(std::memory_order_relaxed); }

    /// Feed a TS packet for PSI processing.
    /// SectionDemux handles PID filtering, section accumulation, CRC, and version tracking.
    void feed_packet(ts::TSPacket& pkt, int64_t packet_idx) noexcept {
        current_packet_idx_ = packet_idx;
        demux_.feedPacket(pkt);
    }

    /// Get program info for C API
    int32_t get_programs(TsDuckProgramInfoNative* out, int32_t max) const noexcept {
        if (!out || max <= 0)
            return 0;

        int32_t count = 0;
        int32_t total = program_count.load(std::memory_order_relaxed);
        for (int32_t i = 0; i < total && count < max; i++) {
            if (!programs[i].active)
                continue;
            const auto& prog = programs[i];
            out[count].program_number = prog.program_number;
            out[count].pmt_pid = prog.pmt_pid;
            out[count].pcr_pid = prog.pcr_pid;
            out[count].stream_count = prog.stream_count;

            bool has_video = false, has_audio = false;
            for (int32_t s = 0; s < prog.stream_count; s++) {
                if (prog.streams[s].is_video)
                    has_video = true;
                if (prog.streams[s].is_audio)
                    has_audio = true;
            }
            out[count].has_video = has_video ? 1 : 0;
            out[count].has_audio = has_audio ? 1 : 0;
            out[count].reserved = 0;
            out[count].reserved2 = 0;
            count++;
        }
        return count;
    }

    void reset() noexcept {
        demux_.reset();
        demux_.addPID(ts::PID_PAT);  // Re-subscribe after reset

        std::fill(programs.begin(), programs.end(), ProgramEntry{});
        program_count.store(0, std::memory_order_release);
        last_pat_packet_idx.store(-1, std::memory_order_release);
        crc_errors.store(0, std::memory_order_release);
    }

    // ========================================================================
    // ts::TableHandlerInterface — called by SectionDemux when a complete,
    // CRC-valid table arrives
    // ========================================================================

    void handleTable(ts::SectionDemux& /*demux*/, const ts::BinaryTable& table) override {
        switch (table.tableId()) {
            case ts::TID_PAT:
                handle_pat(table);
                break;
            case ts::TID_PMT:
                handle_pmt(table);
                break;
            default:
                break;
        }
    }

private:
    ts::DuckContext& duck_;
    ts::SectionDemux demux_{duck_};
    int64_t current_packet_idx_{0};

    void handle_pat(const ts::BinaryTable& table) {
        ts::PAT pat(duck_, table);
        if (!pat.isValid())
            return;

        last_pat_packet_idx.store(current_packet_idx_, std::memory_order_release);

        auto seq = seqlock.begin_write();
        int32_t count = 0;

        for (const auto& [program_number, pmt_pid] : pat.pmts) {
            if (count >= static_cast<int32_t>(MAX_PROGRAMS))
                break;

            programs[count].program_number = static_cast<uint16_t>(program_number);
            programs[count].pmt_pid = static_cast<uint16_t>(pmt_pid);
            programs[count].active = true;

            // Subscribe to this PMT PID for future packets
            demux_.addPID(pmt_pid);
            count++;
        }

        // Deactivate excess entries
        for (int32_t i = count; i < static_cast<int32_t>(MAX_PROGRAMS); i++) {
            programs[i].active = false;
        }

        program_count.store(count, std::memory_order_release);
        seqlock.end_write(seq);
    }

    void handle_pmt(const ts::BinaryTable& table) {
        // Determine which program this PMT belongs to
        ts::PMT pmt(duck_, table);
        if (!pmt.isValid())
            return;

        // Find the matching program slot by service_id (program_number)
        int32_t idx = find_program(pmt.service_id);
        if (idx < 0)
            return;

        auto seq = seqlock.begin_write();
        auto& prog = programs[idx];
        prog.pmt_received = true;
        prog.last_pmt_packet_idx = current_packet_idx_;
        prog.pcr_pid = pmt.pcr_pid;

        int32_t es_count = 0;
        for (const auto& [pid, stream] : pmt.streams) {
            if (es_count >= static_cast<int32_t>(MAX_ELEMENTARY_STREAMS))
                break;

            prog.streams[es_count].pid = pid;
            prog.streams[es_count].stream_type = stream.stream_type;

            bool is_video = false, is_audio = false;
            classify_stream_type(stream.stream_type, is_video, is_audio);
            prog.streams[es_count].is_video = is_video;
            prog.streams[es_count].is_audio = is_audio;
            prog.streams[es_count].active = true;
            es_count++;
        }

        prog.stream_count = es_count;

        // Deactivate excess ES entries
        for (int32_t i = es_count; i < static_cast<int32_t>(MAX_ELEMENTARY_STREAMS); i++) {
            prog.streams[i].active = false;
        }

        seqlock.end_write(seq);
    }

    int32_t find_program(uint16_t service_id) const noexcept {
        int32_t count = program_count.load(std::memory_order_relaxed);
        for (int32_t i = 0; i < count; i++) {
            if (programs[i].active && programs[i].program_number == service_id) {
                return i;
            }
        }
        return -1;
    }
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_PSI_MONITOR_HPP
