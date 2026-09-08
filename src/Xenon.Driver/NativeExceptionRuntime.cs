namespace Xenon.Driver;

internal static class NativeExceptionRuntime
{
    public const string Source = """
        #include <cstddef>
        #include <cstdint>
        #include <cstdio>
        #include <cstdlib>
        #include <cstring>
        #include <exception>
        #include <new>

        namespace {
        struct XenonExceptionRecord {
            XenonExceptionRecord* previous;
            void* allocation;
            void* object;
            const char* type_chain;
            const char* type_name;
            void (*destructor)(void*);
            bool caught;
        };
        struct XenonNativeException { };
        thread_local XenonExceptionRecord* current_exception = nullptr;

        [[noreturn]] void terminate_current() noexcept;

        void destroy_record(XenonExceptionRecord* record) noexcept {
            if (!record) return;
            if (record->destructor) {
                try { record->destructor(record->object); }
                catch (...) { terminate_current(); }
            }
            std::free(record->allocation);
        }

        [[noreturn]] void terminate_current() noexcept {
            XenonExceptionRecord* record = current_exception;
            if (record && record->type_name)
                std::fprintf(stderr, "Unhandled exception of type '%s'\n", record->type_name);
            else
                std::fputs("Unhandled Xenon exception\n", stderr);
            while (record) {
                XenonExceptionRecord* previous = record->previous;
                // Detach before running user destruction. If that destructor throws,
                // terminate_current is entered again and must not destroy this record twice.
                current_exception = previous;
                record->previous = nullptr;
                destroy_record(record);
                record = previous;
            }
            current_exception = nullptr;
            std::fflush(stderr);
            std::_Exit(1);
        }
        }

        extern "C" void* __xenon_eh_allocate(std::uintptr_t size, std::uintptr_t alignment,
                                               const char* type_chain, const char* type_name,
                                               void (*destructor)(void*)) {
            if (alignment == 0) alignment = 1;
            std::size_t bytes = sizeof(XenonExceptionRecord) + static_cast<std::size_t>(alignment - 1) +
                                static_cast<std::size_t>(size);
            void* allocation = std::malloc(bytes);
            if (!allocation) std::abort();
            auto* record = new (allocation) XenonExceptionRecord{};
            std::uintptr_t start = reinterpret_cast<std::uintptr_t>(record + 1);
            std::uintptr_t object = (start + alignment - 1) & ~(alignment - 1);
            record->allocation = allocation;
            record->object = reinterpret_cast<void*>(object);
            record->type_chain = type_chain;
            record->type_name = type_name;
            record->destructor = destructor;
            return record;
        }

        extern "C" void* __xenon_eh_object(void* opaque) {
            return static_cast<XenonExceptionRecord*>(opaque)->object;
        }

        extern "C" void __xenon_eh_activate(void* opaque) {
            auto* record = static_cast<XenonExceptionRecord*>(opaque);
            record->previous = current_exception;
            current_exception = record;
        }

        extern "C" [[noreturn]] void __xenon_eh_throw(void* opaque) {
            __xenon_eh_activate(opaque);
            std::set_terminate(terminate_current);
            throw XenonNativeException{};
        }

        extern "C" void* __xenon_eh_current() {
            if (current_exception) current_exception->caught = true;
            return current_exception;
        }

        extern "C" bool __xenon_eh_matches(void* opaque, const char* requested_chain) {
            auto* record = static_cast<XenonExceptionRecord*>(opaque);
            if (!record || !record->type_chain || !requested_chain) return false;
            std::size_t requested_length = std::strcspn(requested_chain, "\n");
            const char* candidate = record->type_chain;
            while (*candidate) {
                std::size_t candidate_length = std::strcspn(candidate, "\n");
                if (candidate_length == requested_length &&
                    std::memcmp(candidate, requested_chain, requested_length) == 0) return true;
                candidate += candidate_length;
                if (*candidate == '\n') ++candidate;
            }
            return false;
        }

        extern "C" void __xenon_eh_handle(void* opaque) {
            auto* record = static_cast<XenonExceptionRecord*>(opaque);
            if (!record) terminate_current();
            if (record != current_exception) {
                XenonExceptionRecord* candidate = current_exception;
                while (candidate && candidate != record) candidate = candidate->previous;
                // A control transfer from an exceptional finally may already have
                // suppressed and destroyed this catch record.
                if (!candidate) return;
                terminate_current();
            }
            current_exception = record->previous;
            destroy_record(record);
        }

        extern "C" void __xenon_eh_abandon(void* opaque) {
            auto* abandoned = static_cast<XenonExceptionRecord*>(opaque);
            XenonExceptionRecord** link = &current_exception;
            while (*link && *link != abandoned) link = &(*link)->previous;
            if (!*link) return;
            *link = abandoned->previous;
            destroy_record(abandoned);
        }

        extern "C" void __xenon_eh_replace_previous() {
            if (!current_exception || !current_exception->previous) return;
            XenonExceptionRecord* replaced = current_exception->previous;
            current_exception->previous = replaced->previous;
            destroy_record(replaced);
        }

        extern "C" void __xenon_eh_cleanup(void (*destructor)(void*), void* object) noexcept {
            if (!destructor) return;
            try { destructor(object); }
            catch (...) { terminate_current(); }
        }

        extern "C" void __xenon_eh_initialize(void (*initializer)(), unsigned char* guard) {
            try { initializer(); }
            catch (...) {
                if (guard) *guard = 0;
                throw;
            }
        }

        extern "C" [[noreturn]] void __xenon_eh_rethrow() {
            if (!current_exception) terminate_current();
            current_exception->caught = false;
            std::set_terminate(terminate_current);
            throw XenonNativeException{};
        }

        extern "C" [[noreturn]] void __xenon_eh_terminate() { terminate_current(); }
        """;
}
