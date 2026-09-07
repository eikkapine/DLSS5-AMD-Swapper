#include "hip_host_timing.h"

#if defined(BRIDGE_HAVE_HIP_HEADERS)
#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable: 4201) // Anonymous vector unions in the vendor header.
#endif
#include <hip/hip_runtime_api.h>
#if defined(_MSC_VER)
#pragma warning(pop)
#endif
#include <psapi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <stdexcept>
#include <type_traits>
#include <utility>

static_assert(HIP_VERSION_MAJOR == 7, "HIP timing requires the HIP 7 declarations");

namespace {
enum Api : size_t { Launch, DeviceWait, StreamWait, EventWait, Copy, CopyAsync, ApiCount };
constexpr std::array<const char*, ApiCount> kNames{
    "hipLaunchKernel", "hipDeviceSynchronize", "hipStreamSynchronize",
    "hipEventSynchronize", "hipMemcpy", "hipMemcpyAsync"};
constexpr std::array<const char*, ApiCount> kFields{
    "launch", "device_wait", "stream_wait", "event_wait", "copy", "copy_async"};

// All signatures come from the installed HIP header, not reconstructed structs.
decltype(&hipLaunchKernel) originalLaunch = nullptr;
decltype(&hipDeviceSynchronize) originalDeviceWait = nullptr;
decltype(&hipStreamSynchronize) originalStreamWait = nullptr;
decltype(&hipEventSynchronize) originalEventWait = nullptr;
decltype(&hipMemcpy) originalCopy = nullptr;
decltype(&hipMemcpyAsync) originalCopyAsync = nullptr;

struct Counts {
    uint64_t calls = 0;
    uint64_t errors = 0;
    int64_t ticks = 0;
    int64_t maximum = 0;
};
struct ThreadSample {
    int64_t begin = 0;
    std::array<Counts, ApiCount> api{};
    uint64_t nondefaultLaunches = 0;
};
thread_local ThreadSample sample;

// Deliberately process-lifetime: the vendor's worker can outlive main's local
// objects. No callback points at an unloaded library or a destroyed logger.
HANDLE traceFile = INVALID_HANDLE_VALUE;
SRWLOCK traceLock = SRWLOCK_INIT;
struct PendingLine { char text[2048]{}; size_t bytes = 0; };
std::array<PendingLine, 32> pending{};
size_t pendingHead = 0;
size_t pendingCount = 0;
int64_t frequency = 0;
int64_t origin = 0;
size_t writtenBytes = 0;
std::atomic<bool> recording{false};

// These process-lifetime hints never replace GPU synchronization. Only worker
// waits preceded by a successful kernel launch on that same thread qualify.
// The presentation thread's initialization waits are not scheduling signals.
HANDLE progressEvent = nullptr;
DWORD presentationThread = 0;
std::atomic<bool> progressAvailable{false};
std::atomic<uint32_t> activeWorkerWaits{0};
std::atomic<uint64_t> returnedWorkerWaits{0};
thread_local bool queuedKernels = false;

int64_t Tick() noexcept {
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}

bool WriteTrace(const char* text, size_t bytes) noexcept {
    if (writtenBytes + bytes > 8 * 1024 * 1024) {
        recording.store(false, std::memory_order_relaxed);
        return false;
    }
    DWORD written = 0;
    if (!WriteFile(traceFile, text, static_cast<DWORD>(bytes), &written, nullptr) || written != bytes) {
        recording.store(false, std::memory_order_relaxed);
        return false;
    }
    writtenBytes += written;
    return true;
}

void Record(Api api, int64_t begin, int64_t end, hipError_t result, bool nondefault) noexcept {
    const bool finalSample = end - origin >= frequency * 120;
    if (finalSample) recording.store(false, std::memory_order_relaxed);
    if (sample.begin == 0) sample.begin = begin;
    Counts& count = sample.api[api];
    ++count.calls;
    count.errors += result != hipSuccess;
    const int64_t duration = std::max<int64_t>(0, end - begin);
    count.ticks += duration;
    count.maximum = std::max(count.maximum, duration);
    sample.nondefaultLaunches += nondefault;

    // Aggregate per thread; never take a blocking log lock on the HIP path.
    if ((!finalSample && end - sample.begin < frequency) || !TryAcquireSRWLockExclusive(&traceLock)) return;
    if (pendingCount == pending.size()) {
        ReleaseSRWLockExclusive(&traceLock);
        return; // Retain the counters until the presentation thread drains them.
    }
    char line[2048]{};
    const double toMs = 1000.0 / static_cast<double>(frequency);
    int used = std::snprintf(line, sizeof(line),
        "elapsed_s=%.6f thread=%lu window_ms=%.6f nondefault_launches=%llu",
        static_cast<double>(end - origin) / frequency, GetCurrentThreadId(),
        static_cast<double>(end - sample.begin) * toMs,
        static_cast<unsigned long long>(sample.nondefaultLaunches));
    for (size_t i = 0; i < ApiCount && used > 0 && static_cast<size_t>(used) < sizeof(line); ++i) {
        const auto& c = sample.api[i];
        const size_t remaining = sizeof(line) - static_cast<size_t>(used);
        const int added = std::snprintf(line + used, remaining,
            " %s_calls=%llu %s_ms=%.6f %s_max_ms=%.6f %s_errors=%llu",
            kFields[i], static_cast<unsigned long long>(c.calls), kFields[i],
            static_cast<double>(c.ticks) * toMs, kFields[i],
            static_cast<double>(c.maximum) * toMs, kFields[i],
            static_cast<unsigned long long>(c.errors));
        if (added < 0 || static_cast<size_t>(added) >= remaining) { used = -1; break; }
        used += added;
    }
    if (used > 0 && static_cast<size_t>(used) + 1 < sizeof(line)) {
        line[used++] = '\n';
        auto& destination = pending[(pendingHead + pendingCount) % pending.size()];
        destination.bytes = static_cast<size_t>(used);
        std::memcpy(destination.text, line, destination.bytes);
        ++pendingCount;
    }
    sample = {};
    sample.begin = end;
    ReleaseSRWLockExclusive(&traceLock);
}

template<Api api, typename Function, typename... Args>
hipError_t Measure(Function function, bool nondefault, Args&&... args) {
    if (!recording.load(std::memory_order_acquire)) return function(std::forward<Args>(args)...);
    const DWORD incomingError = GetLastError();
    const int64_t begin = Tick();
    SetLastError(incomingError);
    const hipError_t result = function(std::forward<Args>(args)...);
    const DWORD returnedError = GetLastError();
    const int64_t end = Tick();
    Record(api, begin, end, result, nondefault);
    SetLastError(returnedError);
    return result;
}

hipError_t ObservedLaunch(const void* function, dim3 grid, dim3 block, void** arguments,
                          size_t sharedBytes, hipStream_t stream) {
    const auto result = Measure<Launch>(originalLaunch, stream != nullptr,
                                       function, grid, block, arguments, sharedBytes, stream);
    if (result == hipSuccess) queuedKernels = true;
    return result;
}
hipError_t ObservedDeviceWait() {
    const DWORD incomingError = GetLastError();
    const bool announce = queuedKernels && progressAvailable.load(std::memory_order_acquire) &&
                          GetCurrentThreadId() != presentationThread;
    queuedKernels = false;
    if (announce) activeWorkerWaits.fetch_add(1, std::memory_order_acq_rel);
    SetLastError(incomingError);
    const auto result = Measure<DeviceWait>(originalDeviceWait, false);
    const DWORD returnedError = GetLastError();
    if (announce) {
        // Also wake on failure, so the bridge can use its existing error path.
        // The runtime may not have published its ready flag yet; this is only
        // permission to try the established output path, never to bypass it.
        returnedWorkerWaits.fetch_add(1, std::memory_order_release);
        activeWorkerWaits.fetch_sub(1, std::memory_order_release);
        SetEvent(progressEvent);
    }
    SetLastError(returnedError);
    return result;
}
hipError_t ObservedStreamWait(hipStream_t stream) { return Measure<StreamWait>(originalStreamWait, false, stream); }
hipError_t ObservedEventWait(hipEvent_t event) { return Measure<EventWait>(originalEventWait, false, event); }
hipError_t ObservedCopy(void* destination, const void* source, size_t bytes, hipMemcpyKind kind) {
    return Measure<Copy>(originalCopy, false, destination, source, bytes, kind);
}
hipError_t ObservedCopyAsync(void* destination, const void* source, size_t bytes,
                            hipMemcpyKind kind, hipStream_t stream) {
    return Measure<CopyAsync>(originalCopyAsync, false, destination, source, bytes, kind, stream);
}
static_assert(std::is_same_v<decltype(&ObservedLaunch), decltype(&hipLaunchKernel)>);
static_assert(std::is_same_v<decltype(&ObservedDeviceWait), decltype(&hipDeviceSynchronize)>);
static_assert(std::is_same_v<decltype(&ObservedStreamWait), decltype(&hipStreamSynchronize)>);
static_assert(std::is_same_v<decltype(&ObservedEventWait), decltype(&hipEventSynchronize)>);
static_assert(std::is_same_v<decltype(&ObservedCopy), decltype(&hipMemcpy)>);
static_assert(std::is_same_v<decltype(&ObservedCopyAsync), decltype(&hipMemcpyAsync)>);

struct Image {
    unsigned char* base;
    size_t size;
    template<typename T> T* At(size_t offset) const {
        if (offset > size || sizeof(T) > size - offset) throw std::runtime_error("HIP timing: invalid import bounds");
        return reinterpret_cast<T*>(base + offset);
    }
    const char* Text(size_t offset) const {
        const char* text = At<char>(offset);
        if (!std::memchr(text, 0, std::min<size_t>(size - offset, 256)))
            throw std::runtime_error("HIP timing: invalid import name");
        return text;
    }
};

// The only writes are atomic replacements of this module's named import slots.
// No instruction patch, fixed private RVA, DLL-on-disk edit, or GPU call occurs.
void ReplaceSlot(void** slot, void* expected, void* replacement) {
    DWORD previous = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &previous))
        throw std::runtime_error("HIP timing: cannot access import slot");
    void* observed = InterlockedCompareExchangePointer(slot, replacement, expected);
    DWORD ignored = 0;
    if (!VirtualProtect(slot, sizeof(void*), previous, &ignored))
        throw std::runtime_error("HIP timing: cannot restore import page protection; startup stopped");
    if (observed != expected)
        throw std::runtime_error("HIP timing: import changed during setup; startup stopped");
}
} // namespace

const char* StartHipHostTiming(HMODULE neuralModule, bool enabled) {
    if (!enabled) return "disabled_by_option";
    MODULEINFO info{};
    if (!GetModuleInformation(GetCurrentProcess(), neuralModule, &info, sizeof(info)))
        return "unavailable_module_info";
    if (info.SizeOfImage > 256 * 1024 * 1024) return "unsupported_image_size";
    const Image image{static_cast<unsigned char*>(info.lpBaseOfDll), info.SizeOfImage};
    const auto* dos = image.At<IMAGE_DOS_HEADER>(0);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 0) return "unsupported_image";
    const auto* nt = image.At<IMAGE_NT_HEADERS64>(static_cast<size_t>(dos->e_lfanew));
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        nt->OptionalHeader.NumberOfRvaAndSizes <= IMAGE_DIRECTORY_ENTRY_IMPORT) return "unsupported_image";
    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress || directory.Size > image.size) return "missing_imports";
    std::array<void**, ApiCount> slots{};
    for (size_t d = 0; d < directory.Size / sizeof(IMAGE_IMPORT_DESCRIPTOR); ++d) {
        const auto* descriptor = image.At<IMAGE_IMPORT_DESCRIPTOR>(directory.VirtualAddress + d * sizeof(IMAGE_IMPORT_DESCRIPTOR));
        if (!descriptor->Name) break;
        if (_stricmp(image.Text(descriptor->Name), "amdhip64_7.dll") != 0) continue;
        if (!descriptor->OriginalFirstThunk || !descriptor->FirstThunk) return "missing_import_names";
        for (size_t i = 0; i < 4096; ++i) {
            const auto* name = image.At<IMAGE_THUNK_DATA64>(descriptor->OriginalFirstThunk + i * sizeof(IMAGE_THUNK_DATA64));
            auto* slot = image.At<IMAGE_THUNK_DATA64>(descriptor->FirstThunk + i * sizeof(IMAGE_THUNK_DATA64));
            if (!name->u1.AddressOfData) break;
            if (IMAGE_SNAP_BY_ORDINAL64(name->u1.Ordinal)) continue;
            if (name->u1.AddressOfData >= image.size) return "unsupported_import_bounds";
            const char* function = image.Text(static_cast<size_t>(name->u1.AddressOfData) + offsetof(IMAGE_IMPORT_BY_NAME, Name));
            for (size_t n = 0; n < ApiCount; ++n) {
                if (std::strcmp(function, kNames[n]) == 0) slots[n] = reinterpret_cast<void**>(&slot->u1.Function);
            }
        }
    }
    HMODULE hip = GetModuleHandleW(L"amdhip64_7.dll");
    if (!hip) return "missing_hip7_module";
    std::array<void*, ApiCount> originals{};
    for (size_t i = 0; i < ApiCount; ++i) {
        originals[i] = reinterpret_cast<void*>(GetProcAddress(hip, kNames[i]));
        if (!slots[i] || !originals[i] || *slots[i] != originals[i]) return "unsupported_or_already_observed_imports";
    }
    LARGE_INTEGER rate{};
    if (!QueryPerformanceFrequency(&rate) || rate.QuadPart <= 0) return "unavailable_clock";
    frequency = rate.QuadPart;
    origin = Tick();
    wchar_t path[96]{};
    std::swprintf(path, std::size(path), L"bridge-hip-timing-%lu.log", GetCurrentProcessId());
    traceFile = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (traceFile == INVALID_HANDLE_VALUE) return "unavailable_log_file";
    constexpr char header[] =
        "schema=1 method=host_api_wall_clock scope=neural_module_named_imports capture_limit_s=120\n"
        "note=API calls are not neural jobs; synchronization time is not isolated GPU kernel time.\n"
        "note=Each row is one thread's interval; intervals on different threads overlap.\n"
        "note=Timing adds CPU overhead; no kernels, streams, events, copies or waits are added.\n";
    if (!WriteTrace(header, sizeof(header) - 1)) {
        CloseHandle(traceFile);
        traceFile = INVALID_HANDLE_VALUE;
        return "unavailable_log_write";
    }
    originalLaunch = reinterpret_cast<decltype(originalLaunch)>(originals[Launch]);
    originalDeviceWait = reinterpret_cast<decltype(originalDeviceWait)>(originals[DeviceWait]);
    originalStreamWait = reinterpret_cast<decltype(originalStreamWait)>(originals[StreamWait]);
    originalEventWait = reinterpret_cast<decltype(originalEventWait)>(originals[EventWait]);
    originalCopy = reinterpret_cast<decltype(originalCopy)>(originals[Copy]);
    originalCopyAsync = reinterpret_cast<decltype(originalCopyAsync)>(originals[CopyAsync]);
    const std::array<void*, ApiCount> observers{
        reinterpret_cast<void*>(&ObservedLaunch), reinterpret_cast<void*>(&ObservedDeviceWait),
        reinterpret_cast<void*>(&ObservedStreamWait), reinterpret_cast<void*>(&ObservedEventWait),
        reinterpret_cast<void*>(&ObservedCopy), reinterpret_cast<void*>(&ObservedCopyAsync)};
    presentationThread = GetCurrentThreadId();
    // An unavailable hint event leaves ordinary feed pacing in use. No event
    // creation, allocation, file access or extra HIP call occurs per launch.
    progressEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    for (size_t i = 0; i < ApiCount; ++i) ReplaceSlot(slots[i], originals[i], observers[i]);
    recording.store(true, std::memory_order_release);
    progressAvailable.store(progressEvent != nullptr, std::memory_order_release);
    return "active_host_api_timing";
}

HipWorkProgress ReadHipWorkProgress() noexcept {
    if (!progressAvailable.load(std::memory_order_acquire)) return {};
    return {true, activeWorkerWaits.load(std::memory_order_acquire),
            returnedWorkerWaits.load(std::memory_order_acquire)};
}

HANDLE HipWorkProgressEvent() noexcept {
    return progressAvailable.load(std::memory_order_acquire) ? progressEvent : nullptr;
}

void FlushHipHostTiming() noexcept {
    if (traceFile == INVALID_HANDLE_VALUE) return;
    const DWORD savedError = GetLastError();
    // Only main calls this; disk I/O is outside the producer lock and HIP path.
    for (size_t i = 0; i < pending.size(); ++i) {
        if (!TryAcquireSRWLockExclusive(&traceLock)) break;
        if (!pendingCount) {
            ReleaseSRWLockExclusive(&traceLock);
            break;
        }
        const PendingLine line = pending[pendingHead];
        pendingHead = (pendingHead + 1) % pending.size();
        --pendingCount;
        ReleaseSRWLockExclusive(&traceLock);
        if (!WriteTrace(line.text, line.bytes)) break;
    }
    SetLastError(savedError);
}

#else
const char* StartHipHostTiming(HMODULE, bool enabled) {
    return enabled ? "unavailable_at_build" : "disabled_by_option";
}
void FlushHipHostTiming() noexcept {}
HipWorkProgress ReadHipWorkProgress() noexcept { return {}; }
HANDLE HipWorkProgressEvent() noexcept { return nullptr; }
#endif
