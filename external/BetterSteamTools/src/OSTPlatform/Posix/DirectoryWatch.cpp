// Steamy POSIX port additions, GPL-3.0; see repository LICENSE.
#include "include/DirectoryWatch.h"
#include <algorithm>
#include <array>
#include <cerrno>
#include <chrono>
#include <climits>
#include <filesystem>
#include <poll.h>
#include <stdexcept>
#include <unistd.h>
#ifdef __APPLE__
#include <fcntl.h>
#include <map>
#include <sys/event.h>
#else
#include <sys/inotify.h>
#endif

namespace OSTPlatform::DirectoryWatch {
struct Watch::Impl {
    std::string directory;
    int fd = -1;
#ifdef __APPLE__
    int directoryFd = -1;
    using Stamp = std::pair<std::filesystem::file_time_type, uintmax_t>;
    std::map<std::string, Stamp> snapshot;
    std::map<std::string, Stamp> Scan() {
        std::map<std::string, Stamp> result;
        for (const auto& item : std::filesystem::directory_iterator(directory)) {
            std::error_code ec;
            const auto time = item.last_write_time(ec);
            if (ec) continue;
            const auto size = item.is_regular_file(ec) ? item.file_size(ec) : 0;
            if (!ec) result[item.path().filename().string()] = {time, size};
        }
        return result;
    }
#endif
    void Close() {
        if (fd >= 0) close(fd);
        fd = -1;
#ifdef __APPLE__
        if (directoryFd >= 0) close(directoryFd);
        directoryFd = -1;
        snapshot.clear();
#endif
        directory.clear();
    }
    ~Impl() { Close(); }
};
Watch::Watch() : impl_(std::make_unique<Impl>()) {}
Watch::~Watch() = default;
Watch::Watch(Watch&&) noexcept = default;
Watch& Watch::operator=(Watch&&) noexcept = default;
bool Watch::Open(const std::string& directory, uint32_t) {
    if (!impl_) impl_ = std::make_unique<Impl>();
    impl_->Close();
    std::error_code ec;
    if (!std::filesystem::is_directory(directory, ec) || ec) return false;
#ifdef __APPLE__
    impl_->directoryFd = open(directory.c_str(), O_EVTONLY | O_CLOEXEC);
    if (impl_->directoryFd < 0) return false;
    impl_->fd = kqueue();
    if (impl_->fd < 0) { impl_->Close(); return false; }
    fcntl(impl_->fd, F_SETFD, FD_CLOEXEC);
    struct kevent event;
    EV_SET(&event, impl_->directoryFd, EVFILT_VNODE, EV_ADD | EV_CLEAR,
        NOTE_WRITE | NOTE_EXTEND | NOTE_ATTRIB | NOTE_RENAME | NOTE_DELETE | NOTE_REVOKE, 0, nullptr);
    if (kevent(impl_->fd, &event, 1, nullptr, 0, nullptr) < 0) { impl_->Close(); return false; }
#else
    impl_->fd = inotify_init1(IN_NONBLOCK | IN_CLOEXEC);
    if (impl_->fd < 0) return false;
    if (inotify_add_watch(impl_->fd, directory.c_str(),
        IN_CREATE | IN_DELETE | IN_MODIFY | IN_CLOSE_WRITE | IN_MOVED_FROM | IN_MOVED_TO
        | IN_DELETE_SELF | IN_MOVE_SELF | IN_ONLYDIR) < 0) { impl_->Close(); return false; }
#endif
    impl_->directory = directory;
#ifdef __APPLE__
    try { impl_->snapshot = impl_->Scan(); }
    catch (const std::filesystem::filesystem_error&) { impl_->Close(); return false; }
#endif
    return true;
}
bool Watch::IssueRead() { return IsOpen(); }
void Watch::Cancel() { if (impl_) impl_->Close(); }
bool Watch::IsOpen() const { return impl_ && impl_->fd >= 0; }
const std::string& Watch::Directory() const {
    static const std::string empty;
    return impl_ ? impl_->directory : empty;
}
std::vector<Change> Watch::Drain() {
    std::vector<Change> result;
    if (!IsOpen()) return result;
#ifdef __APPLE__
    const timespec timeout{};
    struct kevent event;
    const auto count = kevent(impl_->fd, nullptr, 0, &event, 1, &timeout);
    if (count < 0) throw std::runtime_error("Directory watcher read failed");
    if (count == 0) return result;
    if ((event.flags & EV_ERROR) || (event.fflags & (NOTE_DELETE | NOTE_RENAME | NOTE_REVOKE))) {
        Cancel();
        throw std::runtime_error("Watched directory is no longer available");
    }
    auto next = impl_->Scan();
    for (const auto& [name, stamp] : next) {
        const auto old = impl_->snapshot.find(name);
        if (old == impl_->snapshot.end()) result.push_back({name, ChangeAction::Added});
        else if (old->second != stamp) result.push_back({name, ChangeAction::Modified});
    }
    for (const auto& [name, stamp] : impl_->snapshot)
        if (!next.contains(name)) result.push_back({name, ChangeAction::Removed});
    impl_->snapshot = std::move(next);
#else
    alignas(inotify_event) std::array<char, 65536> buffer;
    // Bound a drain even if another process continuously writes into the watched folder.
    for (unsigned pass = 0; pass < 16; ++pass) {
        const auto count = read(impl_->fd, buffer.data(), buffer.size());
        if (count < 0 && errno == EINTR) continue;
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) break;
        if (count <= 0) throw std::runtime_error("Directory watcher read failed");
        for (size_t offset = 0; offset + sizeof(inotify_event) <= static_cast<size_t>(count);) {
            const auto* event = reinterpret_cast<const inotify_event*>(buffer.data() + offset);
            if (offset + sizeof(inotify_event) + event->len > static_cast<size_t>(count))
                throw std::runtime_error("Truncated directory watcher event");
            if (event->mask & IN_Q_OVERFLOW)
                throw std::runtime_error("Directory watcher queue overflow; rescan required");
            if (event->mask & (IN_DELETE_SELF | IN_MOVE_SELF | IN_IGNORED)) {
                Cancel();
                throw std::runtime_error("Watched directory is no longer available");
            }
            if (event->len && event->name[0]) {
                ChangeAction action = ChangeAction::Modified;
                if (event->mask & IN_MOVED_FROM) action = ChangeAction::RenamedOldName;
                else if (event->mask & IN_MOVED_TO) action = ChangeAction::RenamedNewName;
                else if (event->mask & IN_CREATE) action = ChangeAction::Added;
                else if (event->mask & IN_DELETE) action = ChangeAction::Removed;
                result.push_back({event->name, action});
            }
            offset += sizeof(inotify_event) + event->len;
        }
    }
#endif
    return result;
}
WaitResult WaitAny(std::span<Watch*> watches, uint32_t timeoutMs) {
    std::vector<pollfd> fds;
    fds.reserve(watches.size());
    for (auto* watch : watches)
        fds.push_back({watch && watch->IsOpen() ? watch->impl_->fd : -1, POLLIN, 0});
    if (fds.empty() || std::none_of(fds.begin(), fds.end(), [](auto& fd) { return fd.fd >= 0; }))
        return {WaitStatus::Failed, 0};
    const auto started = std::chrono::steady_clock::now();
    int remaining = timeoutMs == UINT32_MAX ? -1 : static_cast<int>(std::min<uint32_t>(timeoutMs, INT_MAX));
    for (;;) {
        const auto ready = poll(fds.data(), fds.size(), remaining);
        if (ready == 0) return {WaitStatus::Timeout, 0};
        if (ready < 0) {
            if (errno != EINTR) return {WaitStatus::Failed, 0};
            if (timeoutMs != UINT32_MAX) {
                const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - started).count();
                if (elapsed >= timeoutMs) return {WaitStatus::Timeout, 0};
                remaining = static_cast<int>(std::min<uint64_t>(timeoutMs - elapsed, INT_MAX));
            }
            continue;
        }
        for (size_t i = 0; i < fds.size(); ++i) {
            if (fds[i].revents & (POLLERR | POLLNVAL | POLLHUP)) return {WaitStatus::Failed, i};
            if (fds[i].revents & POLLIN) return {WaitStatus::Signaled, i};
        }
    }
}
}
