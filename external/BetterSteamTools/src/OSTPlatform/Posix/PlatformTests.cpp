// Steamy POSIX conformance tests, GPL-3.0; see repository LICENSE.
#include "include/ByteSearch.h"
#include "include/DirectoryWatch.h"
#include "include/DynamicLibrary.h"
#include "include/Encoding.h"
#include "include/Numbers.h"
#include "include/Thread.h"
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <stdexcept>
#include <unistd.h>

static void Check(bool condition, const char* description) {
    if (!condition) throw std::runtime_error(description);
}
int main() {
    namespace fs = std::filesystem;
    using namespace OSTPlatform;
    auto path = fs::temp_directory_path() / ("steamy-bst-platform-" + std::to_string(getpid()));
    if (!fs::create_directory(path)) return 1;
    try {
        const std::string unicode = "Steamy \xc3\xbc \xf0\x9f\x8e\xae";
        Check(Encoding::WideToUtf8(Encoding::Utf8ToWide(unicode)) == unicode, "Unicode round trip");
        for (const auto invalid : {"\xc0\xaf", "\xed\xa0\x80", "\xf4\x90\x80\x80", "\xe2\x82"})
            Check(Encoding::Utf8ToWide(invalid).empty(), "Invalid UTF-8 rejected");
        Check(!Numbers::ParseUInt32("4294967296"), "Integer overflow rejected");
        Check(Numbers::ParseHexUInt64(L"0xffff") == 65535, "Original numeric parser works on POSIX");
        Check(fs::is_regular_file(DynamicLibrary::GetMainExecutablePath()), "Actual executable path");
#ifdef __APPLE__
        auto lib = DynamicLibrary::Load("/usr/lib/libSystem.B.dylib");
#else
        auto lib = DynamicLibrary::Load("libc.so.6");
#endif
        Check(lib && DynamicLibrary::GetSymbol(lib, "getpid"), "Real native library/symbol resolution");
        Check(!DynamicLibrary::GetSymbol(lib, "steamy_nonexistent_export"), "Missing export fails");
        Check(DynamicLibrary::GetSymbol(lib, uint16_t{123}) == nullptr, "PE ordinals unsupported");
        Check(!DynamicLibrary::GetModuleDirectory(lib).empty(), "Native module location");
        DirectoryWatch::Watch watch;
        Check(watch.Open(path.string(), 16384) && watch.IssueRead(), "Watch open");
        std::array<DirectoryWatch::Watch*, 2> watches{nullptr, &watch};
        Check(DirectoryWatch::WaitAny(watches, 20).status == DirectoryWatch::WaitStatus::Timeout, "Idle watch sleeps");
        const auto file = path / "123.lua";
        { std::ofstream out(file, std::ios::binary); out << "abcdefPATTERNend"; }
        auto ready = DirectoryWatch::WaitAny(watches, 2000);
        Check(ready.status == DirectoryWatch::WaitStatus::Signaled && ready.index == 1, "Correct signaled watch");
        const auto changes = watch.Drain();
        Check(std::any_of(changes.begin(), changes.end(), [](auto& c) { return c.relativePath == "123.lua"; }), "Lua filename reported");
        const std::string needle = "PATTERN";
        const std::span pattern(reinterpret_cast<const uint8_t*>(needle.data()), needle.size());
        Check(ByteSearch::FindInFile(file, pattern, 8) == 6, "Pattern spanning read chunks");
        Check(!ByteSearch::FindInFileRange(file, 7, 8, pattern, 8), "Range cannot match earlier bytes");
        Check(!ByteSearch::FindInFileRange(file, 0, 12, pattern, 8), "Range cannot read past end");
        Check(!ByteSearch::FindInFile(file, pattern, 0), "Invalid chunk size rejected");
        watch.Cancel();
        Check(!watch.IsOpen() && DirectoryWatch::WaitAny(watches, 10).status == DirectoryWatch::WaitStatus::Failed, "Cancelled watch fails promptly");
        auto promise = std::make_shared<std::promise<uint32_t>>();
        auto future = promise->get_future();
        Check(Thread::StartDetached([promise]() { promise->set_value(42); return 0; }), "Detached worker starts");
        Check(future.wait_for(std::chrono::seconds(2)) == std::future_status::ready && future.get() == 42, "Detached worker completes");
        fs::remove_all(path); // Unique test-owned directory created above.
        std::cout << "BetterSteamTools POSIX platform conformance passed. Steam hooks not tested.\n";
        return 0;
    } catch (const std::exception& e) {
        fs::remove_all(path);
        std::cerr << e.what() << '\n';
        return 1;
    }
}
