#pragma once
// Exact pinned IOT_LIB.dll PDB callback ABI, MSVC x64 release CRT.
// This header records numeric transport observations only. No packet, identifier,
// timestamp, SDK error text, credentials, file or network operations are retained.
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <stdexcept>
#include <vector>

namespace c500g_message {
enum class Result : int { Continue = 0, Finished = 1 };
enum class CompletionPolicy { WaitForResponse, FinishOnSend };

// The Error template parameter is the bridge's existing SDK error structure.
// ITaskScheduler's sole field is flattened at offset0; there is no vtable.
template<class Error>
struct MessageCallback {
    std::function<void(std::function<void()>)> schedule_on;
    std::function<Result(uint32_t)> on_send;
    std::function<Result(std::vector<uint8_t>, int64_t)> on_ack;
    std::function<Result(std::vector<uint8_t>, int64_t)> on_recv_data;
    std::function<Result(std::shared_ptr<Error>)> on_error;
};

struct NumericStatus {
    std::atomic<uint32_t> queued{0}, sent{0}, responses{0}, errors{0}, timeouts{0};
    std::atomic<int> last_category{0}, last_code{0};
};

// Call only AFTER the public send_data result is0 (request accepted into queue).
// MakeCallback and on_send deliberately do not claim that enqueueing occurred.
inline void RecordQueued(const std::shared_ptr<NumericStatus>& status) noexcept {
    if (status) status->queued.fetch_add(1, std::memory_order_relaxed);
}

// All observations are independent numeric counters. Their relaxed loads are
// diagnostics, not synchronization or proof of physical camera movement.
template<class Error>
inline void AssertLayout() {
    typedef MessageCallback<Error> Callback;
    static_assert(sizeof(void*) == 8, "The pinned SDK requires x64");
    static_assert(sizeof(Result) == 4, "SDK Result enum32");
    static_assert(static_cast<int>(Result::Continue) == 0 && static_cast<int>(Result::Finished) == 1, "SDK Result values");
    static_assert(sizeof(std::function<void()>) == 64, "MSVC release std::function ABI");
    static_assert(sizeof(std::shared_ptr<Error>) == 16, "MSVC shared_ptr ABI");
    static_assert(sizeof(Callback) == 320 && alignof(Callback) == 8, "IMessageCallback PDB layout");
    static_assert(offsetof(Callback, schedule_on) == 0, "schedule_on PDB offset");
    static_assert(offsetof(Callback, on_send) == 64, "on_send PDB offset");
    static_assert(offsetof(Callback, on_ack) == 128, "on_ack PDB offset");
    static_assert(offsetof(Callback, on_recv_data) == 192, "on_recv_data PDB offset");
    static_assert(offsetof(Callback, on_error) == 256, "on_error PDB offset");
    static_assert(sizeof(Error) == 144 && offsetof(Error, cat) == 32 && offsetof(Error, code) == 72, "SDK Error numeric offsets");
}

template<class Error>
inline std::shared_ptr<MessageCallback<Error>> MakeCallback(std::shared_ptr<NumericStatus> status, CompletionPolicy policy = CompletionPolicy::WaitForResponse) {
    AssertLayout<Error>();
    if (!status) throw std::invalid_argument("Numeric callback state is required");
    auto callback = std::make_shared<MessageCallback<Error>>();
    // No scheduler: short nonblocking callbacks run on the invoking SDK thread.
    // on_ack stays empty to match original Android nSendUserData need_ack(false).
    callback->on_send = [status, policy](uint32_t) noexcept -> Result {
        status->sent.fetch_add(1, std::memory_order_relaxed);
        // APK PTZ UserDataCallback(null) finishes on send. Model reads need a response.
        return policy == CompletionPolicy::FinishOnSend ? Result::Finished : Result::Continue;
    };
    callback->on_recv_data = [status](std::vector<uint8_t>, int64_t) noexcept -> Result {
        // Discard all response bytes. A matching response is not semantic success.
        status->responses.fetch_add(1, std::memory_order_relaxed);
        return Result::Finished;
    };
    callback->on_error = [status](std::shared_ptr<Error> error) noexcept -> Result {
        const int category = error ? error->cat : -1;
        const int code = error ? error->code : -1;
        status->last_category.store(category, std::memory_order_relaxed);
        status->last_code.store(code, std::memory_order_relaxed);
        status->errors.fetch_add(1, std::memory_order_relaxed);
        if (category == 3001 && code == 21000)
            status->timeouts.fetch_add(1, std::memory_order_relaxed);
        return Result::Finished;
    };
    return callback;
}
} // namespace c500g_message
