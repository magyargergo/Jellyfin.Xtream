// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONTEXT_CONTEXT_HPP
#define TSDUCK_INTEROP_CONTEXT_CONTEXT_HPP

#include <atomic>
#include <tsduck.h>

namespace tsduck_interop {
namespace context {

struct TsDuckContext {
    ts::DuckContext duck;
    std::atomic<bool> initialized{false};

    TsDuckContext() : duck(nullptr) {
        initialized.store(true, std::memory_order_release);
    }

    bool isInitialized() const noexcept {
        return initialized.load(std::memory_order_acquire);
    }
};

}  // namespace context
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CONTEXT_CONTEXT_HPP
