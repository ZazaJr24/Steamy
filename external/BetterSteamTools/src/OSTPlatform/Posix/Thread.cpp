// Steamy POSIX port additions, GPL-3.0; see repository LICENSE.
#include "include/Thread.h"
#include <pthread.h>
#include <thread>
#include <utility>
namespace OSTPlatform::Thread {
NativeThreadHandle CurrentNativeThreadHandle() {
    return reinterpret_cast<NativeThreadHandle>(pthread_self());
}
bool StartDetached(std::function<uint32_t()> entry) {
    if (!entry) return false;
    try {
        std::thread([entry = std::move(entry)]() {
            try { entry(); } catch (...) { /* Never terminate Steam from a worker exception. */ }
        }).detach();
        return true;
    } catch (...) { return false; }
}
}
