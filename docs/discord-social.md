# Discord, chat, GIFs and voice

Steamy 0.8 uses the official Discord Social SDK, with an optional connection in setup, Friends and Settings → Connections. Discord is independent of Steam. The public application ID is `1559657404994429101`. No bot, client secret, personal account token, own server or hosting is needed.

## Connect and reconnect

Sign-in opens Discord or the system browser with PKCE and the `openid sdk.social_layer` scopes. The application must have Social SDK access, **Public Client** enabled and the exact redirect `http://127.0.0.1/callback` registered. These settings are already supplied for this project; users do not enter a client secret.

An SDK approval failure with RPCError 5000 falls back to standard Discord browser authorization using the SDK-generated S256 verifier/challenge. The callback listener binds only to loopback port 80, validates the callback path/host and a random state, bounds headers and closes after completion or cancellation. Declining access and token/friend/message failures do not open another authorization prompt. Raw OAuth responses, codes and tokens are never included in browser responses or UI diagnostics.

Saved sessions use Windows DPAPI under the existing Steamy data root. Connect automatically restores the matching application in the background; expiry refreshes tokens. Startup and page initialization share one restore operation. Temporary failures retain the session and retry after two and six seconds; revoked authorization clears it. Startup never opens a login prompt or starts voice. Disconnect removes the local credential even offline, clears social state and attempts bounded revocation. The rest of the app stays usable during connection.

## Friends and messages

The charcoal workspace contains contacts, a conversation and a party inspector. The original floating navigation and Home artwork remain. At compact widths the party inspector becomes a popover. Direct/party chat supports requests, online status, search, remove/block/unblock, plain-text messages and the latest 200 history entries. Enter sends; Shift+Enter inserts a line.

Messages show Sending, Sent or Failed. A per-conversation outbox displays pending bubbles and offers Retry/Check/Dismiss. Server echoes and history reconcile using message IDs and Steamy client nonces. Confirmed messages cannot regress to Failed. An uncertain delivery checks history rather than blindly resending. Other conversations can send while history is slow. Drafts and anchored reading positions are retained for up to 32 conversations per session; disconnect clears them. New messages do not pull a reader away from older history. Chat visibility is reported to Discord to avoid redundant Discord sounds.

Notifications cover messages, friend requests, party invitations and completed downloads. The shell bell, in-app toasts and taskbar badge share one bounded center. Reading a conversation clears its badge; active conversations suppress duplicate toasts. Initial history creates no notifications. The center keeps at most 80 entries and three seven-second toasts. Notifications are memory-only and can be muted in Settings. Windows Action Center delivery and notification sounds are not implemented.

## GIFs and images

GIFs animate in chat, the picker and the enlarged viewer using XamlAnimatedGif. The viewer closes with Escape. Offscreen/minimized playback pauses; offscreen downloads cancel and inactive media is released. Up to four GIFs play automatically at once. Reduced effects shows a poster with an explicit Play button. Media uses bounded in-memory streams, with no persistent provider-media cache.

Limits: 8 MiB transfer, three concurrent media downloads, GIF dimensions at most 1536 per axis and 1.5 million logical pixels, 300 frames and 120 million aggregate frame pixels. Posters decode off the UI thread at a bounded size. Only trusted Discord/GIPHY/KLIPY media hosts over HTTPS are accepted; redirects are disabled. Unsupported shared Discord content retains an Open in Discord action.

**KLIPY is the default provider; GIPHY is optional.** The picker has search, trending, pagination, provider-ID favorites, a 300 ms debounce, cancellation, bounded JSON, quota cooldowns and provider attribution. GIPHY uses its unmodified official attribution logo in both themes. Provider order and direct media URLs are preserved. Search API calls go directly to the selected provider. An app key can be entered in GIF search settings; it is a public client configuration key, not a Discord credential.

No provider key was supplied for this build. Embedded search therefore shows a useful configuration state rather than fake results. Trusted direct image/GIF links can be pasted into the composer or picker without a search key. Selecting a GIF adds the link to the draft; it does not send automatically. See [KLIPY's API documentation](https://docs.klipy.com/) and [GIPHY's requirements](https://developers.giphy.com/docs/api/).

## Parties and joining

Choose an installed game to create a lobby. Party codes contain a random 32-byte join secret and a checksum. Typed metadata distinguishes versioned Steamy game parties from private voice lobbies and other applications. Invalid, expired, foreign or empty joins are cleaned up instead of displaying a successful empty party. Existing activity invitations retain a validated compatibility path.

Copy a party code to your friend. They open Steamy, connect Discord and use Join with code in Friends. A Discord activity invitation is received by the connected desktop app, then accepted explicitly. Desktop activation registers only Steamy's own executable and a fixed `--discord-join` marker. Joining an invitation does not launch a program. Launch game resolves the App ID against installed local library entries and uses the validated launch service; remote names/paths/arguments never become executable instructions.

The party organizes friends and shared chat/voice. The game's multiplayer implementation still handles its own lobby/server connection. Two real accounts and supported desktop clients are required to validate invitation delivery and launch activation.

## Voice

Private calls use a separate two-person lobby. A DM invitation binds the application, host, guest, random secret and five-minute expiry. Your own/expired invitations cannot be accepted. The recipient explicitly joins. Party voice uses the game lobby and is limited to the SDK-recommended participant count. Invalid membership, a closed lobby, expired unanswered invitations and a departing peer end private calls.

Microphone capture starts only after an explicit call/join action. Options are applied while initially muted. One active call can be switched through an explicit action. The persistent call bar survives navigation and provides mute, deafen, leave, microphone/output selection, input/output volume, per-participant volume and speaker indicators. Push-to-talk offers F8/F9/F10/V and continues while a game has focus. Logout, app exit and session changes cancel operations and dispose the native voice session. A restart never rejoins with an open microphone. [Official voice guide](https://docs.discord.com/developers/discord-social-sdk/development-guides/managing-voice-chat).

The protocol, view model, fake-call WPF UI, device-error handling and native SDK lifecycle are tested. **Real audio between two PCs/accounts has not been tested.** Device switching, acoustic quality, real packet loss and two-account invitation acceptance remain live checks.

## Native execution and developer setup

All native SDK calls and callbacks run on a dedicated STA dispatcher, separate from WPF. Immutable snapshots cross to the UI. History/network awaits do not hold a global client gate; operation leases, cancellation and generations prevent stale results from replacing current state. Relationship updates are coalesced. The SDK's native free callback delegate remains rooted for the entire runtime lifetime.

Import the developer-supplied official Unity SDK ZIP or extracted package before a Discord-enabled build:

```powershell
./scripts/import-discord-sdk.ps1 -SdkPath 'C:\Downloads\DiscordSocialSdk-UnityPlugin-VERSION.zip'
dotnet build src/Steamy/Steamy.csproj -c Release
```

The importer adapts the official generated C# bindings for .NET 10: WPF callback pumping replaces Unity PlayerLoop registration; desktop JIT removes Mono AOT annotations. Native Windows companions, model files, official notices and a digest manifest are retained under ignored `tools/DiscordSocialSdk/runtime`, then copied to `Tools/Discord` in build/publish. The current package uses SDK 1.10.19337. A build without it remains usable and clearly reports Discord unavailable. A public application ID can be overridden through the `DiscordApplicationId` MSBuild property. Discord Comms Access and application/privacy limits still apply.

For current results and explicitly unperformed live tests, see [the implementation checklist](steamy-0.8-progress.md) and [verification report](steamy-0.8-report.md).
