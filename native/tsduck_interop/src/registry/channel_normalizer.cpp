// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "channel_registry.hpp"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <regex>
#include <string>

namespace tsduck_interop::registry {

namespace {

// ============================================================================
// Pre-compiled Regex Patterns (matching C# NormalizationPatterns)
// ============================================================================

// Country/region prefixes: "PL:", "PL |", "|PL|", "[PL]", "(PL)", "PL-"
// Note: Simplified from C# - removed Unicode stars/arrows as they're less common
const std::regex kCountryPrefixPattern(
    R"(^(\d+\s+)?([A-Z]{2,3}\s*[\|:\-]|\|[A-Z]{2,3}\||\[[A-Z]{2,3}\]|\([A-Z]{2,3}\)|[A-Z]{2,3}-)\s*)",
    std::regex::icase | std::regex::optimize);

// Quality indicators: HD, FHD, 4K, 1080p, etc.
const std::regex kQualityIndicatorPattern(
    R"(\b(HD|FHD|SD|4K\+?|8K|UHD|HEVC|H\.?265|H\.?264|1080[PI]?|720[PI]?|480[PI]?|576[PI]?|2160[PI]?|MULTI|DUAL|AAC|AC3|DTS|DOLBY|ATMOS)\b)",
    std::regex::icase | std::regex::optimize);

// Streaming suffixes: Live, Stream, Backup, etc.
const std::regex kStreamingSuffixPattern(
    R"(\s+(Live|Stream|Streaming|Online|24/7|247|Backup|Main|Primary|Secondary|Alt|Alternative)\s*$)",
    std::regex::icase | std::regex::optimize);

// Country name suffixes (simplified - most common ones)
const std::regex kCountrySuffixPattern(
    R"(\s+(Poland|Polska|PL|UK|Germany|Deutschland|DE|France|FR|Spain|Espana|ES|Italy|Italia|IT|Netherlands|Nederland|NL|USA|US|Canada|CA|Australia|AU|Russia|RU|Turkey|TR|Greece|GR)\s*$)",
    std::regex::icase | std::regex::optimize);

// Additional noise patterns
const std::regex kNoisePattern(
    R"(\b(NEW|VIP|PREMIUM|MULTI|AUDIO|DUBBED|SUBBED|ORIGINAL|OV|VO|VOST|PPV|EVENT|SPECIAL|PROMO|TEST|DEMO|SAMPLE)\b|\[[^\]]*\]|\([^)]*\)|#\d+|:\s*$)",
    std::regex::icase | std::regex::optimize);

// Word normalization patterns
const std::regex kSportsPattern(R"(\bSPORTS\b)", std::regex::icase | std::regex::optimize);
const std::regex kMoviesPattern(R"(\bMOVIES\b)", std::regex::icase | std::regex::optimize);
const std::regex kStandaloneTvPattern(R"((^|[^A-Za-z])TV([^A-Za-z0-9+]|$))", std::regex::icase | std::regex::optimize);
const std::regex kNationalGeographicPattern(R"(\bNATIONAL\s*GEOGRAPHIC\b)", std::regex::icase | std::regex::optimize);
const std::regex kNatGeoPattern(R"(\bNAT[\s\-]*GEO\b)", std::regex::icase | std::regex::optimize);
const std::regex kTravelChannelPattern(R"(\bTRAVEL\s*CHANNEL\b)", std::regex::icase | std::regex::optimize);
const std::regex kEEntertainmentPattern(R"(\bE!\s*ENTERTAINMENT\b)", std::regex::icase | std::regex::optimize);
const std::regex kStandaloneChannelPattern(R"(\s+CHANNEL\b)", std::regex::icase | std::regex::optimize);

// Non-alphanumeric removal
const std::regex kNonAlphanumericPattern(R"([^A-Za-z0-9])", std::regex::optimize);

// ============================================================================
// Quality Score Patterns (matching C# QualityScorer)
// ============================================================================

struct QualityPattern {
    const char* pattern;
    int32_t score;
};

// Sorted by score (highest first) for early exit
constexpr QualityPattern kQualityPatterns[] = {
    {"4K", QUALITY_SCORE_4K},
    {"UHD", QUALITY_SCORE_4K},
    {"2160", QUALITY_SCORE_4K},
    {"FHD", QUALITY_SCORE_FHD},
    {"1080", QUALITY_SCORE_FHD},
    {"FULLHD", QUALITY_SCORE_FHD},
    {"HD", QUALITY_SCORE_HD},
    {"720", QUALITY_SCORE_HD},
    {"SD", QUALITY_SCORE_SD},
    {"576", QUALITY_SCORE_SD},
    {"480", QUALITY_SCORE_SD},
    {"LQ", QUALITY_SCORE_SD - 10},
};

// ============================================================================
// Diacritics Mapping (matching C# SpecialCharacterRule)
// ============================================================================

char map_diacritic(char32_t c) {
    // Common diacritics to base character mapping
    switch (c) {
        // A variants
        case U'ą': case U'Ą': case U'à': case U'À': case U'á': case U'Á':
        case U'â': case U'Â': case U'ã': case U'Ã': case U'ä': case U'Ä':
        case U'å': case U'Å': case U'æ': case U'Æ':
            return 'a';

        // C variants
        case U'ć': case U'Ć': case U'ç': case U'Ç': case U'č': case U'Č':
            return 'c';

        // E variants
        case U'ę': case U'Ę': case U'è': case U'È': case U'é': case U'É':
        case U'ê': case U'Ê': case U'ë': case U'Ë':
            return 'e';

        // I variants
        case U'ì': case U'Ì': case U'í': case U'Í': case U'î': case U'Î':
        case U'ï': case U'Ï':
            return 'i';

        // L variants
        case U'ł': case U'Ł':
            return 'l';

        // N variants
        case U'ń': case U'Ń': case U'ñ': case U'Ñ': case U'ň': case U'Ň':
            return 'n';

        // O variants
        case U'ó': case U'Ó': case U'ò': case U'Ò': case U'ô': case U'Ô':
        case U'õ': case U'Õ': case U'ö': case U'Ö': case U'ø': case U'Ø':
            return 'o';

        // S variants
        case U'ś': case U'Ś': case U'š': case U'Š': case U'ş': case U'Ş':
            return 's';

        // U variants
        case U'ù': case U'Ù': case U'ú': case U'Ú': case U'û': case U'Û':
        case U'ü': case U'Ü':
            return 'u';

        // Y variants
        case U'ý': case U'Ý': case U'ÿ': case U'Ÿ':
            return 'y';

        // Z variants
        case U'ź': case U'Ź': case U'ż': case U'Ż': case U'ž': case U'Ž':
            return 'z';

        // D variants
        case U'đ': case U'Đ':
            return 'd';

        default:
            return 0;  // Not a known diacritic
    }
}

// ============================================================================
// Helper Functions
// ============================================================================

/// Process special characters (separators, diacritics, ampersand)
std::string process_special_chars(const std::string& input) {
    std::string result;
    result.reserve(input.size());

    for (size_t i = 0; i < input.size(); ) {
        unsigned char c = static_cast<unsigned char>(input[i]);

        // ASCII fast path
        if (c < 0x80) {
            switch (c) {
                // Separators -> space
                case '_': case '-': case '.': case '/': case '\\': case '|':
                    result += ' ';
                    break;

                // Keep alphanumeric and spaces
                case ' ':
                    result += ' ';
                    break;

                // Preserve plus sign
                case '+':
                    result += '+';
                    break;

                // Ampersand -> "and"
                case '&':
                    result += " and ";
                    break;

                default:
                    if (std::isalnum(c)) {
                        result += static_cast<char>(c);
                    }
                    // Skip other non-alphanumeric
                    break;
            }
            ++i;
        } else {
            // UTF-8 multi-byte character
            // Decode UTF-8 to get codepoint
            char32_t codepoint = 0;
            int bytes = 0;

            if ((c & 0xE0) == 0xC0) {
                bytes = 2;
                codepoint = c & 0x1F;
            } else if ((c & 0xF0) == 0xE0) {
                bytes = 3;
                codepoint = c & 0x0F;
            } else if ((c & 0xF8) == 0xF0) {
                bytes = 4;
                codepoint = c & 0x07;
            } else {
                // Invalid UTF-8, skip
                ++i;
                continue;
            }

            if (i + bytes > input.size()) {
                // Incomplete sequence
                ++i;
                continue;
            }

            for (int j = 1; j < bytes; ++j) {
                codepoint = (codepoint << 6) | (input[i + j] & 0x3F);
            }

            // Try to map diacritic
            char mapped = map_diacritic(codepoint);
            if (mapped != 0) {
                result += mapped;
            } else if (codepoint == U'ß') {
                result += "ss";
            } else if (codepoint == U'þ' || codepoint == U'Þ') {
                result += "th";
            }
            // Skip other non-ASCII characters

            i += bytes;
        }
    }

    return result;
}

/// Collapse multiple whitespace to single space and trim
std::string collapse_whitespace(const std::string& input) {
    std::string result;
    result.reserve(input.size());

    bool last_was_space = true;  // Start true to trim leading

    for (char c : input) {
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
            if (!last_was_space) {
                result += ' ';
                last_was_space = true;
            }
        } else {
            result += c;
            last_was_space = false;
        }
    }

    // Trim trailing space
    if (!result.empty() && result.back() == ' ') {
        result.pop_back();
    }

    return result;
}

/// Convert to uppercase and remove non-alphanumeric
std::string to_uppercase_alphanumeric(const std::string& input) {
    std::string result;
    result.reserve(input.size());

    for (char c : input) {
        if (std::isalnum(static_cast<unsigned char>(c))) {
            result += static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
        }
    }

    return result;
}

/// Check if name contains pattern as whole word (word boundary check)
bool contains_word(const std::string& name, const char* pattern) {
    std::string upper_name;
    upper_name.reserve(name.size());
    for (char c : name) {
        upper_name += static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    }

    size_t pattern_len = std::strlen(pattern);
    size_t pos = 0;

    while ((pos = upper_name.find(pattern, pos)) != std::string::npos) {
        // Check word boundary before
        bool start_ok = (pos == 0) ||
                        !std::isalnum(static_cast<unsigned char>(upper_name[pos - 1]));

        // Check word boundary after
        size_t end_pos = pos + pattern_len;
        bool end_ok = (end_pos >= upper_name.size()) ||
                      !std::isalnum(static_cast<unsigned char>(upper_name[end_pos]));

        if (start_ok && end_ok) {
            return true;
        }

        ++pos;
    }

    return false;
}

}  // namespace

// ============================================================================
// Public API Implementation
// ============================================================================

std::string normalize_channel_name(const std::string& input) {
    if (input.empty()) {
        return "";
    }

    std::string result = input;

    // 1. Strip country prefixes
    result = std::regex_replace(result, kCountryPrefixPattern, "");

    // 2. Strip quality indicators
    result = std::regex_replace(result, kQualityIndicatorPattern, "");

    // 3. Strip streaming suffixes
    result = std::regex_replace(result, kStreamingSuffixPattern, "");

    // 4. Strip country name suffixes
    result = std::regex_replace(result, kCountrySuffixPattern, "");

    // 5. Strip noise patterns
    result = std::regex_replace(result, kNoisePattern, "");

    // 6. Process special characters (diacritics, separators, ampersand)
    result = process_special_chars(result);

    // 7. Collapse whitespace
    result = collapse_whitespace(result);

    // 8. Word normalization
    result = std::regex_replace(result, kSportsPattern, "SPORT");
    result = std::regex_replace(result, kMoviesPattern, "MOVIE");
    result = std::regex_replace(result, kStandaloneTvPattern, "");
    result = std::regex_replace(result, kNationalGeographicPattern, "NATGEO");
    result = std::regex_replace(result, kNatGeoPattern, "NATGEO");
    result = std::regex_replace(result, kTravelChannelPattern, "TRAVEL");
    result = std::regex_replace(result, kEEntertainmentPattern, "E");
    result = std::regex_replace(result, kStandaloneChannelPattern, "");

    // 9. Collapse whitespace again (word normalization may have created gaps)
    result = collapse_whitespace(result);

    // 10. Final cleanup: uppercase alphanumeric only
    result = to_uppercase_alphanumeric(result);

    return result;
}

int32_t calculate_quality_score(const std::string& name, bool has_icon) {
    if (name.empty()) {
        return has_icon ? QUALITY_SCORE_UNKNOWN + QUALITY_SCORE_HAS_ICON
                        : QUALITY_SCORE_UNKNOWN;
    }

    // Check patterns in order (sorted by score, highest first)
    for (const auto& pattern : kQualityPatterns) {
        if (contains_word(name, pattern.pattern)) {
            return has_icon ? pattern.score + QUALITY_SCORE_HAS_ICON
                            : pattern.score;
        }
    }

    return has_icon ? QUALITY_SCORE_UNKNOWN + QUALITY_SCORE_HAS_ICON
                    : QUALITY_SCORE_UNKNOWN;
}

// ============================================================================
// ChannelRegistry Static Methods
// ============================================================================

bool ChannelRegistry::normalize_name(const char* input, char* output, size_t output_size) {
    if (!input || !output || output_size == 0) {
        return false;
    }

    std::string result = normalize_channel_name(input);

    if (result.empty()) {
        output[0] = '\0';
        return false;
    }

    // Copy to output buffer
    size_t copy_len = std::min(result.size(), output_size - 1);
    std::memcpy(output, result.c_str(), copy_len);
    output[copy_len] = '\0';

    return true;
}

int32_t ChannelRegistry::calculate_quality_score(const char* name, bool has_icon) {
    if (!name) {
        return has_icon ? QUALITY_SCORE_UNKNOWN + QUALITY_SCORE_HAS_ICON
                        : QUALITY_SCORE_UNKNOWN;
    }

    return registry::calculate_quality_score(name, has_icon);
}

}  // namespace tsduck_interop::registry
