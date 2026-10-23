# Work from GitHub without exposing provider keys

The public repository contains the application, compiled relay client DLL,
and server implementation. The DLL implementation source is kept locally. It contains no live provider or SSH credentials.

1. Clone the repository or open a fresh workspace from GitHub.
2. Install the .NET SDK selected by `global.json` and restore/build `Steamy.sln`.
   Project references copy the supplied `lib/Steamy.RelayClient.dll`.
3. Push application changes. CI checks the source for credentials, builds the
   application and ControllerHost, and runs the app and relay tests using fixtures.
4. For a release, update project versions and CHANGELOG, then create its `v*` tag
   or run Build & Release. Version 0.8.0 publishes the portable ZIP, app payload,
   setup installer. The DLL is in the repository and app package.

No provider secret is needed to build the app locally or in GitHub Actions.
The DLL constructs requests to the public HTTPS relay; the VPS attaches its own
provider key to fixed upstream requests. Real keys remain in the protected
`/etc/steamy-relay/relay.env` on that server. Server code can be maintained in this
repository and deployed independently; publishing source does not overwrite the
server's environment file.

The standard app works without login or activation on another PC. Its public
protocol can also be implemented by other clients. An EXE, DLL, obfuscation or
compiled-in shared password cannot provide an original-app-only access guarantee.
Server-side request, concurrency, generation and transfer limits constrain use.
See [provider-relay.md](provider-relay.md) for the exact limits and trust model.

Personal keys explicitly entered by a user keep their direct-provider behavior
and Windows-protected local storage. Those personal keys are separate from the
server's shared provider credentials.
