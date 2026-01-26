// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONTEXT_CONTEXT_HPP
#define TSDUCK_INTEROP_CONTEXT_CONTEXT_HPP

#include <atomic>
#include <tsduck.h>

namespace tsduck_interop::context {

/// Wrapper around ts::DuckContext providing the TsDuck library context
/// needed by SectionDemux, PAT/PMT deserialization, and other TsDuck APIs.
///
/// Previously, DuckContext caused EDEADLK because the Names singleton
/// couldn't find its data files (only .so files were deployed to runtime).
/// Fix: deploy /opt/tsduck/share/tsduck alongside the libraries.
struct TsDuckContext {
    ts::DuckContext duck;
    std::atomic<bool> initialized{false};

    TsDuckContext() { initialized.store(true, std::memory_order_release); }

    bool is_initialized() const noexcept { return initialized.load(std::memory_order_acquire); }
};

}  // namespace tsduck_interop::context

#endif  // TSDUCK_INTEROP_CONTEXT_CONTEXT_HPP
