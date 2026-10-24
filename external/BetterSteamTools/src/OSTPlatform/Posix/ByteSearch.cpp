// Steamy POSIX port additions, GPL-3.0; see repository LICENSE.
#include "include/ByteSearch.h"
#include <algorithm>
#include <fstream>
#include <functional>
#include <limits>
#include <vector>
namespace OSTPlatform::ByteSearch {
std::optional<size_t> Find(std::span<const uint8_t> bytes, std::span<const uint8_t> pattern) {
    if (pattern.empty() || pattern.size() > bytes.size()) return std::nullopt;
    auto found = std::search(bytes.begin(), bytes.end(),
        std::boyer_moore_horspool_searcher(pattern.begin(), pattern.end()));
    if (found == bytes.end()) return std::nullopt;
    return static_cast<size_t>(found - bytes.begin());
}
std::optional<uint64_t> FindInFileRange(const std::filesystem::path& path, uint64_t offset,
    uint64_t size, std::span<const uint8_t> pattern, size_t chunkBytes) {
    if (pattern.empty() || size < pattern.size() || chunkBytes == 0) return std::nullopt;
    std::error_code ec;
    const auto length = std::filesystem::file_size(path, ec);
    if (ec || offset >= length || offset > static_cast<uint64_t>(std::numeric_limits<std::streamoff>::max()))
        return std::nullopt;
    size = std::min(size, length - offset);
    if (size < pattern.size()) return std::nullopt;
    const auto chunk = std::max(pattern.size(), std::min<size_t>(chunkBytes, 8 * 1024 * 1024));
    if (chunk > std::numeric_limits<size_t>::max() - pattern.size()) return std::nullopt;
    std::vector<uint8_t> bytes(chunk + pattern.size() - 1);
    std::ifstream file(path, std::ios::binary);
    if (!file.seekg(static_cast<std::streamoff>(offset))) return std::nullopt;
    size_t carry = 0;
    uint64_t consumed = 0;
    while (consumed < size) {
        const auto requested = static_cast<std::streamsize>(std::min<uint64_t>(chunk, size - consumed));
        file.read(reinterpret_cast<char*>(bytes.data() + carry), requested);
        const auto count = file.gcount();
        if (count <= 0) return std::nullopt;
        const auto available = carry + static_cast<size_t>(count);
        if (auto found = Find(std::span(bytes.data(), available), pattern))
            return offset + consumed - carry + *found;
        carry = std::min(pattern.size() - 1, available);
        std::copy(bytes.begin() + available - carry, bytes.begin() + available, bytes.begin());
        consumed += static_cast<uint64_t>(count);
    }
    return std::nullopt;
}
std::optional<uint64_t> FindInFile(const std::filesystem::path& path,
    std::span<const uint8_t> pattern, size_t chunkBytes) {
    return FindInFileRange(path, 0, std::numeric_limits<uint64_t>::max(), pattern, chunkBytes);
}
}
