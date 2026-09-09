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
#include <cmath>
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

// Resolve only exports of the HIP DLL already loaded by the runtime. Event
// allocation occurs on the normal worker after a successful existing wait.
// Use timed dispatch, not hipEventRecord(NULL), which can introduce default-
// stream barriers. Flags remain zero: any-order execution is never requested.
decltype(&hipExtLaunchKernel) timedLaunch = nullptr;
decltype(&hipEventCreateWithFlags) createTimingEvent = nullptr;
decltype(&hipEventElapsedTime) elapsedTimingEvents = nullptr;
decltype(&hipGetDevice) samplingGetDevice = nullptr;
decltype(&hipGetFuncBySymbol) samplingGetFunction = nullptr;
decltype(&hipKernelNameRef) samplingKernelName = nullptr;
std::atomic<bool> kernelSamplingAvailable{false};
std::atomic<uint32_t> samplingOwners{0}, samplingAttempts{0}, samplingDropped{0};
uintptr_t neuralImageBase = 0;
size_t neuralImageSize = 0;

struct KernelSample {
    hipEvent_t begin = nullptr, end = nullptr;
    bool initialized = false, disabled = false, armed = false;
    bool selected = false, launchSucceeded = false;
    int device = -1;
    uint32_t launches = 0, target = 0, nextTarget = 0;
    uint64_t completedWaits = 0;
    const void* function = nullptr;
    dim3 grid{}, block{};
    size_t sharedBytes = 0;
};
// Handles intentionally survive thread exit. At most four workers receive two
// events each, retained until process teardown even after a failed GPU wait.
thread_local KernelSample kernelSample;

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

template<size_t Capacity>
void QueueKernelLine(const char (&text)[Capacity], int bytes) noexcept {
    // snprintf returns the required length on truncation, not the number of
    // valid source bytes. Check the caller's buffer as well as the queue slot.
    if (bytes <= 0 || static_cast<size_t>(bytes) >= Capacity ||
        static_cast<size_t>(bytes) >= sizeof(PendingLine::text)) return;
    if (!TryAcquireSRWLockExclusive(&traceLock)) {
        samplingDropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    if (pendingCount < pending.size()) {
        auto& line = pending[(pendingHead + pendingCount) % pending.size()];
        line.bytes = static_cast<size_t>(bytes);
        std::memcpy(line.text, text, line.bytes);
        ++pendingCount;
    } else {
        samplingDropped.fetch_add(1, std::memory_order_relaxed);
    }
    ReleaseSRWLockExclusive(&traceLock);
}

void DisableKernelSampling(const char* reason, hipError_t status) noexcept {
    if (kernelSample.disabled) return;
    kernelSample.disabled = true;
    kernelSample.armed = false;
    kernelSample.selected = false;
    kernelSample.launchSucceeded = false;
    char line[256]{};
    const int bytes = std::snprintf(line, sizeof(line),
        "gpu_sampling_disabled thread=%lu reason=%s status=%d\n",
        GetCurrentThreadId(), reason, static_cast<int>(status));
    QueueKernelLine(line, bytes);
}

bool SelectKernelSample(const void* function, dim3 grid, dim3 block,
                        size_t sharedBytes, hipStream_t stream) noexcept {
    if (!kernelSamplingAvailable.load(std::memory_order_acquire) ||
        GetCurrentThreadId() == presentationThread) return false;
    auto& state = kernelSample;
    if (state.disabled) return false;
    // Count the unmodified sequence even before sampling starts. Positions
    // rotate across actual launch counts, not a hard-coded 156-kernel model.
    const uint32_t ordinal = state.launches++;
    if (!state.armed || ordinal != state.target || stream != nullptr) return false;
    state.armed = false;
    if (!recording.load(std::memory_order_acquire) || Tick() - origin >= frequency * 90) {
        state.disabled = true;
        return false;
    }
    // Reserve a bounded attempt without incrementing past the limit when
    // multiple workers reach their selected ordinal concurrently.
    uint32_t attempts = samplingAttempts.load(std::memory_order_relaxed);
    while (attempts < 512 && !samplingAttempts.compare_exchange_weak(
               attempts, attempts + 1, std::memory_order_relaxed)) {}
    if (attempts >= 512) {
        state.disabled = true;
        return false;
    }
    int currentDevice = -1;
    const hipError_t deviceStatus = samplingGetDevice(&currentDevice);
    if (deviceStatus != hipSuccess || currentDevice != state.device) {
        DisableKernelSampling("device_changed_or_unavailable", deviceStatus);
        return false;
    }
    state.function = function;
    state.grid = grid;
    state.block = block;
    state.sharedBytes = sharedBytes;
    state.selected = true;
    state.launchSucceeded = false;
    return true;
}

void FinishKernelSample(hipError_t waitStatus, bool workerWait) noexcept {
    if (!kernelSamplingAvailable.load(std::memory_order_acquire) ||
        GetCurrentThreadId() == presentationThread) return;
    auto& state = kernelSample;
    if (state.disabled) return;
    const uint32_t launches = state.launches;
    state.launches = 0;
    state.armed = false;
    if (waitStatus != hipSuccess) {
        DisableKernelSampling("existing_device_wait_failed", waitStatus);
        return; // Do not query, reuse, or destroy possibly in-flight events.
    }
    if (!workerWait) return;
    ++state.completedWaits;
    if (state.selected && state.launchSucceeded) {
        int currentDevice = -1;
        const hipError_t deviceStatus = samplingGetDevice(&currentDevice);
        if (deviceStatus != hipSuccess || currentDevice != state.device) {
            DisableKernelSampling("completion_device_changed_or_unavailable", deviceStatus);
            return;
        }
        // The original device-wide wait has already completed. ElapsedTime
        // reads those completed timestamps; no event wait or query loop is added.
        float milliseconds = 0.0f;
        const hipError_t timingStatus = elapsedTimingEvents(&milliseconds, state.begin, state.end);
        if (timingStatus != hipSuccess || !std::isfinite(milliseconds) || milliseconds < 0.0f) {
            DisableKernelSampling("elapsed_time_unavailable", timingStatus);
        } else {
            char name[256] = "unresolved";
            if (samplingGetFunction && samplingKernelName) {
                hipFunction_t function = nullptr;
                const auto status = samplingGetFunction(&function, state.function);
                if (status == hipSuccess) {
                    const char* resolved = samplingKernelName(function);
                    if (resolved && *resolved) {
                        size_t i = 0;
                        for (; i + 1 < sizeof(name) && resolved[i]; ++i) {
                            const unsigned char c = static_cast<unsigned char>(resolved[i]);
                            name[i] = ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                                       (c >= '0' && c <= '9') || c == '_') ? static_cast<char>(c) : '_';
                        }
                        name[i] = 0;
                    }
                } else {
                    DisableKernelSampling("symbol_lookup_failed", status);
                }
            }
            const uintptr_t address = reinterpret_cast<uintptr_t>(state.function);
            const bool inImage = address >= neuralImageBase && address - neuralImageBase < neuralImageSize;
            char line[1024]{};
            const int bytes = std::snprintf(line, sizeof(line),
                "gpu_kernel elapsed_s=%.6f thread=%lu wait_index=%llu ordinal=%u launches_in_wait=%u "
                "kernel=%s kernel_rva=%llu rva_valid=%u grid_x=%u grid_y=%u grid_z=%u "
                "block_x=%u block_y=%u block_z=%u shared_bytes=%llu gpu_ms=%.6f "
                "stream_default=1 sampling_attempts=%u sampling_dropped=%u\n",
                static_cast<double>(Tick() - origin) / frequency, GetCurrentThreadId(),
                static_cast<unsigned long long>(state.completedWaits), state.target, launches, name,
                static_cast<unsigned long long>(inImage ? address - neuralImageBase : 0), inImage ? 1u : 0u,
                state.grid.x, state.grid.y, state.grid.z, state.block.x, state.block.y, state.block.z,
                static_cast<unsigned long long>(state.sharedBytes), static_cast<double>(milliseconds),
                samplingAttempts.load(std::memory_order_relaxed), samplingDropped.load(std::memory_order_relaxed));
            QueueKernelLine(line, bytes);
        }
    }
    state.selected = false;
    state.launchSucceeded = false;
    const int64_t sinceStart = Tick() - origin;
    if (state.disabled) return;
    if (!recording.load(std::memory_order_acquire) || sinceStart >= frequency * 90 ||
        samplingAttempts.load(std::memory_order_relaxed) >= 512) {
        state.disabled = true; // No more counters, queries or clock reads on this worker.
        return;
    }
    if (sinceStart < frequency * 15 || launches == 0 || launches > 4096) return;
    if (!state.initialized) {
        state.initialized = true;
        if (samplingOwners.fetch_add(1, std::memory_order_relaxed) >= 4) {
            DisableKernelSampling("worker_limit", hipSuccess);
            return;
        }
        auto status = samplingGetDevice(&state.device);
        if (status == hipSuccess) status = createTimingEvent(&state.begin, hipEventDefault);
        if (status == hipSuccess) status = createTimingEvent(&state.end, hipEventDefault);
        if (status != hipSuccess) {
            DisableKernelSampling("event_initialization_failed", status);
            return;
        }
    }
    state.target = state.nextTarget % launches;
    state.nextTarget = (state.target + 1) % launches;
    state.armed = true;
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
    const DWORD incomingError = GetLastError();
    const bool sampled = SelectKernelSample(function, grid, block, sharedBytes, stream);
    SetLastError(incomingError);
    // Exactly one dispatch. Never retry a failed timed launch through the
    // ordinary API: a failure is not proof the device submitted no work.
    const auto result = sampled
        ? Measure<Launch>(timedLaunch, false, function, grid, block, arguments, sharedBytes, stream,
                          kernelSample.begin, kernelSample.end, 0)
        : Measure<Launch>(originalLaunch, stream != nullptr, function, grid, block, arguments, sharedBytes, stream);
    const DWORD returnedError = GetLastError();
    if (sampled) {
        kernelSample.launchSucceeded = result == hipSuccess;
        if (result != hipSuccess) DisableKernelSampling("timed_launch_failed", result);
    }
    if (result == hipSuccess) queuedKernels = true;
    SetLastError(returnedError);
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
    FinishKernelSample(result, announce);
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

const char* StartHipHostTiming(HMODULE neuralModule, bool enabled, bool sampleKernels) {
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
    if (sampleKernels) {
        timedLaunch = reinterpret_cast<decltype(timedLaunch)>(GetProcAddress(hip, "hipExtLaunchKernel"));
        createTimingEvent = reinterpret_cast<decltype(createTimingEvent)>(GetProcAddress(hip, "hipEventCreateWithFlags"));
        elapsedTimingEvents = reinterpret_cast<decltype(elapsedTimingEvents)>(GetProcAddress(hip, "hipEventElapsedTime"));
        samplingGetDevice = reinterpret_cast<decltype(samplingGetDevice)>(GetProcAddress(hip, "hipGetDevice"));
        samplingGetFunction = reinterpret_cast<decltype(samplingGetFunction)>(GetProcAddress(hip, "hipGetFuncBySymbol"));
        samplingKernelName = reinterpret_cast<decltype(samplingKernelName)>(GetProcAddress(hip, "hipKernelNameRef"));
    }
    neuralImageBase = reinterpret_cast<uintptr_t>(info.lpBaseOfDll);
    neuralImageSize = info.SizeOfImage;
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
        "bridge_version=" BRIDGE_BUILD_VERSION "\n"
        "schema=1 method=host_api_wall_clock scope=neural_module_named_imports capture_limit_s=120\n"
        "note=API calls are not neural jobs; synchronization time is not isolated GPU kernel time.\n"
        "note=Each row is one thread's interval; intervals on different threads overlap.\n"
        "note=Host timing adds overhead. Optional GPU sampling times one existing launch with flags=0; no kernels, copies, streams or waits are added.\n"
        "note=GPU samples use hipExtLaunchKernel start/stop events, not hipEventRecord on the default stream. Events may perturb scheduling.\n"
        "note=gpu_kernel rows are sampled dispatch timings across different worker intervals, not one complete inference timeline.\n";
    if (!WriteTrace(header, sizeof(header) - 1)) {
        CloseHandle(traceFile);
        traceFile = INVALID_HANDLE_VALUE;
        return "unavailable_log_write";
    }
    const bool canSample = sampleKernels && timedLaunch && createTimingEvent && elapsedTimingEvents && samplingGetDevice;
    char samplingHeader[256]{};
    const int samplingHeaderBytes = std::snprintf(samplingHeader, sizeof(samplingHeader),
        "kernel_sampling=%s since_s=15 until_s=90 max_samples=512 max_workers=4 events_per_worker=2\n",
        canSample ? "bounded_timed_launch" : sampleKernels ? "unavailable_exports" : "disabled_by_option");
    if (samplingHeaderBytes > 0) WriteTrace(samplingHeader, static_cast<size_t>(samplingHeaderBytes));
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
    // An unavailable hint event leaves ordinary feed pacing in use. GPU sample
    // events are created later, on the worker, after an existing successful wait.
    progressEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    for (size_t i = 0; i < ApiCount; ++i) ReplaceSlot(slots[i], originals[i], observers[i]);
    recording.store(true, std::memory_order_release);
    progressAvailable.store(progressEvent != nullptr, std::memory_order_release);
    kernelSamplingAvailable.store(canSample, std::memory_order_release);
    return "active_host_api_timing";
}

HipWorkProgress ReadHipWorkProgress() noexcept {
    if (!progressAvailable.load(std::memory_order_acquire)) return {};
    return {true, activeWorkerWaits.load(std::memory_order_acquire),
            returnedWorkerWaits.load(std::memory_order_acquire)};
}

HipKernelTimingProgress ReadHipKernelTimingProgress() noexcept {
    if (!kernelSamplingAvailable.load(std::memory_order_acquire)) return {};
    return {true, samplingAttempts.load(std::memory_order_relaxed),
            samplingDropped.load(std::memory_order_relaxed)};
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
const char* StartHipHostTiming(HMODULE, bool enabled, bool) {
    return enabled ? "unavailable_at_build" : "disabled_by_option";
}
void FlushHipHostTiming() noexcept {}
HipWorkProgress ReadHipWorkProgress() noexcept { return {}; }
HipKernelTimingProgress ReadHipKernelTimingProgress() noexcept { return {}; }
HANDLE HipWorkProgressEvent() noexcept { return nullptr; }
#endif
