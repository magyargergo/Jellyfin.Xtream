// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_MPEGTS_PACKET_UTILS_HPP
#define TSDUCK_INTEROP_MPEGTS_PACKET_UTILS_HPP

#include <cstdint>
#include "../core/constants.hpp"

namespace tsduck_interop {
namespace mpegts {

// ============================================================================
// PTS/DTS Extraction
// ============================================================================
//
// PTS/DTS format: 5 bytes
// Byte 0: '00xx' (4 bits) + PTS[32..30] (3 bits) + marker (1 bit)
// Byte 1: PTS[29..22] (8 bits)
// Byte 2: PTS[21..15] (7 bits) + marker (1 bit)
// Byte 3: PTS[14..7] (8 bits)
// Byte 4: PTS[6..0] (7 bits) + marker (1 bit)

inline int64_t extractPts(const uint8_t* data) noexcept {
    return (static_cast<int64_t>((data[0] >> 1) & 0x07) << 30) |
           (static_cast<int64_t>(data[1]) << 22) |
           (static_cast<int64_t>((data[2] >> 1)) << 15) |
           (static_cast<int64_t>(data[3]) << 7) |
           (static_cast<int64_t>(data[4] >> 1));
}

// ============================================================================
// PCR Extraction
// ============================================================================
//
// PCR format: 6 bytes
// Bytes 0-3: PCR base[32..1]
// Byte 4: PCR base[0] (1 bit) + reserved (6 bits) + PCR ext[8] (1 bit)
// Byte 5: PCR ext[7..0] (8 bits)

inline int64_t extractPcrBase(const uint8_t* pcr) noexcept {
    return (static_cast<int64_t>(pcr[0]) << 25) |
           (static_cast<int64_t>(pcr[1]) << 17) |
           (static_cast<int64_t>(pcr[2]) << 9) |
           (static_cast<int64_t>(pcr[3]) << 1) |
           ((pcr[4] >> 7) & 0x01);
}

inline int32_t extractPcrExtension(const uint8_t* pcr) noexcept {
    return ((pcr[4] & 0x01) << 8) | pcr[5];
}

// ============================================================================
// PTS/DTS Patching (In-Place Modification)
// ============================================================================

inline void patchPts(uint8_t* pts, int64_t offset_90khz) noexcept {
    // Extract current value
    int64_t value = extractPts(pts);

    // Apply offset with wraparound
    value = (value + offset_90khz) & PTS_33BIT_MAX;

    // Write back preserving marker bits and prefix
    uint8_t prefix = pts[0] & 0xF0;
    pts[0] = static_cast<uint8_t>(prefix | ((value >> 29) & 0x0E) | 0x01);
    pts[1] = static_cast<uint8_t>(value >> 22);
    pts[2] = static_cast<uint8_t>(((value >> 14) & 0xFE) | 0x01);
    pts[3] = static_cast<uint8_t>(value >> 7);
    pts[4] = static_cast<uint8_t>(((value << 1) & 0xFE) | 0x01);
}

// ============================================================================
// PCR Patching (In-Place Modification)
// ============================================================================

inline void patchPcr(uint8_t* pcr, int64_t offset_90khz) noexcept {
    // Extract PCR base (33 bits)
    int64_t pcr_base = extractPcrBase(pcr);

    // Extract extension (9 bits) - keep unchanged
    int32_t pcr_ext = extractPcrExtension(pcr);

    // Apply offset
    pcr_base = (pcr_base + offset_90khz) & PTS_33BIT_MAX;

    // Write back
    pcr[0] = static_cast<uint8_t>(pcr_base >> 25);
    pcr[1] = static_cast<uint8_t>(pcr_base >> 17);
    pcr[2] = static_cast<uint8_t>(pcr_base >> 9);
    pcr[3] = static_cast<uint8_t>(pcr_base >> 1);
    pcr[4] = static_cast<uint8_t>(((pcr_base & 0x01) << 7) | 0x7E | ((pcr_ext >> 8) & 0x01));
    pcr[5] = static_cast<uint8_t>(pcr_ext & 0xFF);
}

// ============================================================================
// PTS Difference with Wraparound Handling
// ============================================================================

inline int64_t ptsDiff(int64_t pts_a, int64_t pts_b) noexcept {
    int64_t diff = pts_a - pts_b;
    if (diff > PTS_33BIT_MAX / 2) {
        diff -= PTS_33BIT_MAX + 1;
    } else if (diff < -PTS_33BIT_MAX / 2) {
        diff += PTS_33BIT_MAX + 1;
    }
    return diff;
}

// ============================================================================
// Packet Header Parsing
// ============================================================================

struct PacketHeader {
    uint16_t pid;
    uint8_t continuity_counter;
    bool transport_error;
    bool payload_unit_start;
    bool transport_priority;
    uint8_t adaptation_field_control;
    bool has_adaptation_field;
    bool has_payload;
    bool is_scrambled;
};

inline PacketHeader parseHeader(const uint8_t* packet) noexcept {
    PacketHeader h;
    h.transport_error = (packet[1] & 0x80) != 0;
    h.payload_unit_start = (packet[1] & 0x40) != 0;
    h.transport_priority = (packet[1] & 0x20) != 0;
    h.pid = static_cast<uint16_t>(((packet[1] & 0x1F) << 8) | packet[2]);
    h.is_scrambled = (packet[3] & 0xC0) != 0;
    h.adaptation_field_control = (packet[3] >> 4) & 0x03;
    h.has_adaptation_field = (h.adaptation_field_control & 0x02) != 0;
    h.has_payload = (h.adaptation_field_control & 0x01) != 0;
    h.continuity_counter = packet[3] & 0x0F;
    return h;
}

// ============================================================================
// Stream Type Detection
// ============================================================================

inline bool isVideoPid(int32_t stream_type) noexcept {
    // MPEG-1/2/4, H.264/AVC, H.265/HEVC
    return stream_type == 0x01 || stream_type == 0x02 ||
           stream_type == 0x10 || stream_type == 0x1B ||
           stream_type == 0x24;
}

inline bool isAudioPid(int32_t stream_type) noexcept {
    // MPEG-1/2 Audio, AAC, AC-3, E-AC-3
    return stream_type == 0x03 || stream_type == 0x04 ||
           stream_type == 0x0F || stream_type == 0x11 ||
           stream_type == 0x81 || stream_type == 0x87;
}

}  // namespace mpegts
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_MPEGTS_PACKET_UTILS_HPP
