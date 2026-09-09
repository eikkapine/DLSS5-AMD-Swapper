#pragma once

#include <windows.h>
#include <cstdint>

// Observes the loaded neural module's HIP imports. Optional bounded sampling
// uses HIP's timed-launch API for one existing kernel per worker wait interval.
// No kernel is added/replayed, and no stream, dimensions, arguments or wait changes.
// Call once, before creating the neural swapchain. Callbacks live until exit.
const char* StartHipHostTiming(HMODULE neuralModule, bool enabled, bool sampleKernels = true);

// Drain bounded in-memory samples during the bridge's existing cadence log
// update. HIP workers perform no file I/O and never wait for this drain.
void FlushHipHostTiming() noexcept;

// Scheduling hints from the existing worker's device wait. A returned wait is
// NOT a published neural image: the normal Present, copy, fences and exact
// image comparison must still run. Hints remain available after tracing ends.
struct HipWorkProgress {
    bool available = false;
    uint32_t activeWaits = 0;
    uint64_t returnedWaits = 0;
};
HipWorkProgress ReadHipWorkProgress() noexcept;
HANDLE HipWorkProgressEvent() noexcept;

// Counts diagnostic attempts, not neural jobs. Compare cadence intervals with
// no new attempts separately from intervals affected by timed dispatches.
struct HipKernelTimingProgress {
    bool available = false;
    uint32_t attempts = 0;
    uint32_t dropped = 0;
};
HipKernelTimingProgress ReadHipKernelTimingProgress() noexcept;
