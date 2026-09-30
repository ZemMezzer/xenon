using Xenon.Compiler;

namespace Xenon.Driver;

/// <summary>Root continuation lifetime and wake notification. No Task, worker pool or scheduler dependency.</summary>
internal static class NativeAsyncRuntime
{
    public static readonly string Source = $$"""

        #include <atomic>
        #include <mutex>
        #include <condition_variable>

        namespace {
        struct AsyncRoot {
            std::atomic<std::size_t> references{1};
            std::mutex mutex;
            std::condition_variable wake;
            bool notified = false;
            bool closed = false;
        };
        }
        extern "C" void* {{RuntimeAbiNames.AsyncRootCreate}}() {
            return new AsyncRoot();
        }
        extern "C" void {{RuntimeAbiNames.AsyncRootRetain}}(void* opaque) {
            static_cast<AsyncRoot*>(opaque)->references.fetch_add(1, std::memory_order_relaxed);
        }
        extern "C" void {{RuntimeAbiNames.AsyncRootRelease}}(void* opaque) {
            auto* root = static_cast<AsyncRoot*>(opaque);
            if (root->references.fetch_sub(1, std::memory_order_acq_rel) == 1) delete root;
        }
        extern "C" void {{RuntimeAbiNames.AsyncRootNotify}}(void* opaque) {
            auto* root = static_cast<AsyncRoot*>(opaque);
            std::lock_guard<std::mutex> lock(root->mutex);
            if (!root->closed) { root->notified = true; root->wake.notify_one(); }
        }
        extern "C" void {{RuntimeAbiNames.AsyncRootPump}}(void* opaque) {
            auto* root = static_cast<AsyncRoot*>(opaque);
            std::unique_lock<std::mutex> lock(root->mutex);
            root->wake.wait(lock, [root] { return root->closed || root->notified; });
            root->notified = false;
        }
        extern "C" void {{RuntimeAbiNames.AsyncRootClose}}(void* opaque) {
            auto* root = static_cast<AsyncRoot*>(opaque);
            {
                std::lock_guard<std::mutex> lock(root->mutex);
                root->closed = true;
                root->wake.notify_all();
            }
            {{RuntimeAbiNames.AsyncRootRelease}}(root);
        }
        """;
}
