#include "include/Memory.h"

#include "include/Log.h"

#include <windows.h>
#include <psapi.h>

namespace OSTPlatform::Memory {

std::optional<ModuleImage> GetModuleImage(DynamicLibrary::ModuleHandle module) {
    MODULEINFO info{};
    if (!module || !GetModuleInformation(GetCurrentProcess(), reinterpret_cast<HMODULE>(module), &info, sizeof(info))) {
        OSTP_LOG_DEBUG("GetModuleImage(module={}) failed (error={})", module, GetLastError());
        return std::nullopt;
    }

    return ModuleImage{
        static_cast<uint8_t*>(info.lpBaseOfDll),
        static_cast<size_t>(info.SizeOfImage),
    };
}

namespace {

    // PAGE_* protections that permit a read. PAGE_NOACCESS and bare
    // PAGE_EXECUTE (execute-only) are absent on purpose: neither can be read.
    bool IsReadableProtect(DWORD protect) {
        // Strip the modifier bits before comparing — they combine with the
        // base protection rather than replacing it.
        const DWORD base = protect & ~static_cast<DWORD>(PAGE_GUARD | PAGE_NOCACHE | PAGE_WRITECOMBINE);
        switch (base) {
        case PAGE_READONLY:
        case PAGE_READWRITE:
        case PAGE_WRITECOPY:
        case PAGE_EXECUTE_READ:
        case PAGE_EXECUTE_READWRITE:
        case PAGE_EXECUTE_WRITECOPY:
            return true;
        default:
            return false;
        }
    }

} // namespace

bool IsReadable(const void* addr, size_t bytes) {
    if (!addr || bytes == 0) return false;

    const uintptr_t start = reinterpret_cast<uintptr_t>(addr);
    if (start > UINTPTR_MAX - bytes) return false;   // wraps
    const uintptr_t end = start + bytes;

    for (uintptr_t cursor = start; cursor < end; ) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (VirtualQuery(reinterpret_cast<LPCVOID>(cursor), &mbi, sizeof(mbi)) != sizeof(mbi)) {
            OSTP_LOG_TRACE("IsReadable({}, {}): VirtualQuery failed at 0x{:X} (error={})",
                           addr, bytes, cursor, GetLastError());
            return false;
        }
        // A guard page faults once on first touch, so reading it is not safe
        // even though its base protection says otherwise.
        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) || !IsReadableProtect(mbi.Protect)) {
            OSTP_LOG_TRACE("IsReadable({}, {}): 0x{:X} not readable (state=0x{:X} protect=0x{:X})",
                           addr, bytes, cursor, mbi.State, mbi.Protect);
            return false;
        }

        const uintptr_t regionEnd = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
        if (regionEnd <= cursor) return false;   // no forward progress; refuse to spin
        cursor = regionEnd;
    }
    return true;
}

bool WriteExecutableByte(void* target, uint8_t value) {
    if (!target) {
        OSTP_LOG_WARN("WriteExecutableByte: target is null");
        return false;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(target, 1, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        OSTP_LOG_WARN("WriteExecutableByte(target={}) VirtualProtect(RWX) failed (error={})",
                      target, GetLastError());
        return false;
    }
    *static_cast<uint8_t*>(target) = value;

    DWORD ignored = 0;
    if (!VirtualProtect(target, 1, oldProtect, &ignored)) {
        OSTP_LOG_WARN("WriteExecutableByte(target={}) VirtualProtect(restore=0x{:X}) failed (error={})",
                      target, oldProtect, GetLastError());
        return false;
    }
    if (!FlushInstructionCache(GetCurrentProcess(), target, 1)) {
        OSTP_LOG_WARN("WriteExecutableByte(target={}) FlushInstructionCache failed (error={})",
                      target, GetLastError());
        return false;
    }
    return true;
}

} // namespace OSTPlatform::Memory
