// Steamy POSIX port additions, GPL-3.0; see repository LICENSE.
#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include "include/DynamicLibrary.h"
#include <dlfcn.h>
#include <cerrno>
#include <mutex>
#include <unordered_map>
#include <vector>
#ifdef __APPLE__
#include <mach-o/dyld.h>
#else
#include <link.h>
#include <unistd.h>
#endif

namespace OSTPlatform::DynamicLibrary {
namespace {
thread_local uint32_t lastError = 0;
std::mutex pathsMutex;
std::unordered_map<void*, std::filesystem::path> paths;
}
ModuleHandle Load(const std::filesystem::path& path) {
    auto handle = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    lastError = handle ? 0 : ENOENT;
    if (handle) {
        std::lock_guard lock(pathsMutex);
        paths[handle] = path;
    }
    return handle;
}
ModuleHandle GetLoaded(std::string_view moduleName) {
    const std::string name(moduleName);
    auto handle = dlopen(name.c_str(), RTLD_NOW | RTLD_NOLOAD);
    lastError = handle ? 0 : ENOENT;
    if (handle) {
        // NOLOAD still takes a reference. Keep only the loader's existing ownership.
        dlclose(handle);
    }
    return handle;
}
void* GetSymbol(ModuleHandle module, const char* name) {
    if (!module || !name) { lastError = EINVAL; return nullptr; }
    dlerror();
    auto symbol = dlsym(module, name);
    lastError = dlerror() ? ENOENT : 0;
    return symbol;
}
void* GetSymbol(ModuleHandle, uint16_t) {
    lastError = ENOTSUP; // PE ordinal exports do not exist in ELF/Mach-O.
    return nullptr;
}
uint32_t GetLastErrorCode() { return lastError; }
std::string GetCurrentDirectoryPath() {
    std::error_code ec;
    auto path = std::filesystem::current_path(ec);
    return ec ? std::string{} : path.string();
}
std::string GetSystemDirectoryPath() { return {}; }
std::filesystem::path GetMainExecutablePath() {
#ifdef __APPLE__
    uint32_t size = 0;
    _NSGetExecutablePath(nullptr, &size);
    std::vector<char> buffer(size);
    if (_NSGetExecutablePath(buffer.data(), &size) != 0) return {};
    std::error_code ec;
    auto path = std::filesystem::canonical(buffer.data(), ec);
    return ec ? std::filesystem::path(buffer.data()) : path;
#else
    std::vector<char> buffer(4096);
    while (buffer.size() <= 1024 * 1024) {
        const auto count = readlink("/proc/self/exe", buffer.data(), buffer.size());
        if (count < 0) return {};
        if (static_cast<size_t>(count) < buffer.size())
            return std::string(buffer.data(), static_cast<size_t>(count));
        buffer.resize(buffer.size() * 2);
    }
    return {};
#endif
}
std::filesystem::path GetModuleDirectory(ModuleHandle module) {
    if (!module) return GetMainExecutablePath().parent_path();
#ifndef __APPLE__
    link_map* map = nullptr;
    if (dlinfo(module, RTLD_DI_LINKMAP, &map) == 0 && map && map->l_name && *map->l_name)
        return std::filesystem::absolute(map->l_name).parent_path();
#endif
    std::lock_guard lock(pathsMutex);
    const auto found = paths.find(module);
    return found == paths.end() ? std::filesystem::path{}
        : std::filesystem::absolute(found->second).parent_path();
}
}
