# Discord removed from the normal app — 21 October 2026

The normal build has Discord integration disabled. Friends/Community navigation, Discord settings and onboarding login are unavailable. Startup does not resolve the social/voice models or reconnect saved Discord accounts. The native Social SDK is excluded from the published package. Controller navigation skips Friends. Download notifications remain available.

The implementation is retained as inactive source for restoration. There is no user-facing toggle to enable it. `EnableDiscordIntegration` defaults to `false`; explicit developer builds can opt in with `-p:EnableDiscordIntegration=true`, with the SDK installed. Settings and saved credentials are not erased by this change.

## Backup

`E:\Steamy\backups\discord-before-removal-20261021-213933\`

- `source-before-discord-removal.zip`: snapshot of the source before this change, including existing uncommitted edits.
- `portable-with-discord\Steamy.exe`: copy of the last working portable build. Keep its entire directory together.
- `backup-manifest.json`: archive hash and verified app DLL hash.

To run the previous version, use that backed-up portable directory. To restore the earlier implementation, recover the source snapshot into a separate checkout and follow the original build instructions. Keep later source changes separate when restoring.

## Verification

The dedicated removal check uses the actual disabled build metadata. It verifies absent social/voice service registrations, hidden entry points, blocked social routes, no voice bar, preserved download notification navigation, no Discord search result in Settings, and the five-step onboarding flow including backward navigation. The archived integration's tests can explicitly opt into its test configuration; the shipped app has no such override.

Final validation: dedicated disabled-flow check passed; 306/306 UI/integration regression checks and 412/412 core checks passed. The retained integration is enabled only inside its legacy test harness; the separate disabled-flow check resets that override and verifies the shipped build's default metadata. Five real portable startup checks passed, including existing profile/library/notes/synthetic encrypted credential preservation and tracker exit 0. No Discord runtime directory is present in the new portable folder. The archive and backed-up portable DLL hashes were rechecked after packaging.

New portable: `E:\Steamy\artifacts\performance-refresh\portable-without-discord\Steamy.exe`. Keep its full directory together. Portable DLL SHA-256: `50123EF426AB24D8713231BEADAAFC4F56B1F42229CF6779EE07862B8ACC76D5`. Detailed evidence: `artifacts/performance-refresh/release-without-discord-validation.json`. These are functional checks, not acceptance of the earlier FPS/RAM targets.
