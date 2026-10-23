# Steamy 0.8.0

Changes since 0.7.1:

- Design: black base, glass surfaces, ambient artwork, softer banner fades, cleaner spacing and sharper text.
- Game page: centered header, compact actions, playtime and last launch; Details → Preview → Description; screenshots, reviews, achievements and a Back button that stays accessible while scrolling.
- Library: collapsible heading, pinned filters, base-game filtering, denser covers, improved artwork fallbacks, Steam logos, search and sorting improvements.
- Images: shared loading pipeline, bounded caches, fixes for loading stalls, cancellation of obsolete requests and cleanup of unused image references.
- Performance: fewer unnecessary updates, deferred settings loading, page reuse, improved reference cleanup and smoother scroll timing with less repeated layout work.
- Downloads: compact queue rows, progress/speed/ETA improvements, resume fixes and clearer step-by-step setup.
- Controllers and tools: clearer selection, guided setup, controller-backend integration, mapping/profile settings and explicit driver checks and installation actions.
- Steam-Plus: select a source or import a local ZIP/Lua file.
- Provider security: shared keys remain on the VPS; server-side request, concurrency and transfer limits constrain usage.
- Packaging: .NET 10, included portable runtime, automated GitHub builds/tests, and Setup.exe plus Portable.zip downloads. The compiled relay DLL is included in the app and repository.
- Discord: integration removed from the current build; backup retained. Friends, chat and voice are not available in this release.
- Hypervisor: marked as planned for 0.8.2, not included in 0.8.0.

Controller hardware validation remains pending.

[Setup.exe](https://github.com/ZazaJr24/Steamy/releases/download/v0.8.0/Steamy-v0.8.0-Setup.exe) · [Portable.zip](https://github.com/ZazaJr24/Steamy/releases/download/v0.8.0/Steamy-v0.8.0.zip)
