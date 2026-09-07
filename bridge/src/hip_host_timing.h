#pragma once

#include <windows.h>
#include <cstdint>

// Observes the loaded neural module's existing HIP imports. It never launches
// GPU work or changes an API's arguments, result, stream, or synchronization.
// Call once, before creating the neural swapchain. Callbacks live until exit.
const char* StartHipHostTiming(HMODULE neuralModule, bool enabled);

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
