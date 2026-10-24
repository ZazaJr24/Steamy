# STEAMY 0.8.0 IS HERE

## FREE DEPOTBOX ACCESS FOR EVERYONE — RYUU PLANNED FOR TOMORROW

**DepotBox is now available FREE directly inside Steamy. Everyone can use the integration without entering or buying a personal API key.**

**Free Ryuu access is planned for tomorrow, October 24.** It will use the same in-app workflow without requiring your own API key once enabled. Ryuu is not available through the shared connection yet.

The shared provider keys stay on the VPS. Server usage limits apply.

## A bigger update since 0.7.1

### Design
- Black base, glass surfaces and ambient artwork.
- Softer banner fades and cleaner transitions.
- Refined spacing, buttons, navigation and text rendering.

### Game page
- Centered game header with compact actions.
- Playtime and last launch shown alongside the game.
- Updated layout: Details → Preview → Description.
- Screenshot previews, expanded image viewing and available Steam reviews and achievements.
- The Back button stays accessible while scrolling.

### Library
- Collapsible heading with pinned filters.
- Base-game filtering and a denser cover grid.
- Better artwork fallbacks and Steam logos.
- Improvements to searching, sorting and filtering.

### Images and performance
- Shared image-loading pipeline and bounded caches.
- Fixes for loading stalls and cancellation of obsolete requests.
- Fewer unnecessary updates and deferred settings loading.
- Cleanup of page/image references and reuse of frequently visited pages.
- More consistent scroll timing and less repeated library layout work.

### Downloads, controllers and tools
- Compact download rows with improved progress, speed and ETA reporting.
- Queue/resume improvements and clearer step-by-step setup.
- Clearer controller/tool selection, profile and mapping settings, and explicit driver checks.
- Steam-Plus source selection or import of your own ZIP/Lua file.

### Technical changes
- Updated to .NET 10, with the required runtime included in the portable package.
- Automated builds and tests through GitHub.
- Shared provider keys remain server-side instead of being embedded in the EXE.

### Removed and coming next
- Discord integration has been removed from this build; the backup is retained. Friends, chat and voice are not available in 0.8.0.
- Full controller hardware validation remains pending.

## Download

**Setup:** https://github.com/ZazaJr24/Steamy/releases/download/v0.8.0/Steamy-v0.8.0-Setup.exe

**Portable:** https://github.com/ZazaJr24/Steamy/releases/download/v0.8.0/Steamy-v0.8.0.zip

Try the update and share your feedback on the interface, performance and tools. Thanks for testing Steamy.
