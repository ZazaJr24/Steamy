using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Steamy.Services;

/// <summary>
/// Loads Discord's own generated C# bindings and native Social SDK. Keeping this optional
/// lets portable/offline builds run without the SDK. No private REST relationship endpoints.
/// SDK API: https://docs.discord.com/developers/discord-social-sdk/getting-started/using-unity
/// </summary>
internal sealed partial class DiscordSdkClient : IDiscordSocialClient, IDiscordCommunicationClient
{
    private sealed record Runtime(Assembly Assembly, IntPtr Native, Action Pump);
    private static readonly ConcurrentDictionary<string, Runtime> Runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Assembly _assembly;
    private readonly object _client;
    private readonly DispatcherTimer _pump;
    private readonly List<Delegate> _callbacks = [];
    private TaskCompletionSource? _ready;
    private bool _connected;
    private bool _disposed;
    private ulong _applicationId;
    public DiscordSnapshot Snapshot { get; private set; } = DiscordSnapshot.Disconnected;
    public event EventHandler? Changed;
    public event EventHandler? TokenExpired;

    public DiscordSdkClient(string directory)
    {
        try
        {
            var runtime = Runtimes.GetOrAdd(Path.GetFullPath(directory), LoadRuntime);
            _assembly = runtime.Assembly;
            _client = New("Client");
            // Create and pump the official bindings on WPF's dispatcher; the generated
            // Unity player-loop registration is replaced by this host during import.
            SetCallback("SetStatusChangedCallback", args =>
            {
                var status = args[0]?.ToString();
                var error = args[1]?.ToString();
                _connected = status == "Ready";
                if (_connected) { RefreshSnapshot(); _ready?.TrySetResult(); }
                else
                {
                    Snapshot = DiscordSnapshot.Disconnected;
                    Changed?.Invoke(this, EventArgs.Empty);
                    if (error != "None") _ready?.TrySetException(new DiscordSocialException("Discord couldn't connect. Check your connection and try again."));
                }
            });
            SetCallback("SetRelationshipGroupsUpdatedCallback", _ => RefreshSnapshot());
            SetCallback("SetUserUpdatedCallback", _ => RefreshSnapshot());
            SetCallback("SetTokenExpirationCallback", _ => TokenExpired?.Invoke(this, EventArgs.Empty));
            InitializeCommunicationCallbacks();
            InitializeVoiceCallbacks();
            _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
            _pump.Tick += (_, _) => runtime.Pump();
            _pump.Start();
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or TypeLoadException
            or MissingMethodException or EntryPointNotFoundException or DllNotFoundException or TargetInvocationException)
        { throw new DiscordSocialException("Discord support couldn't start. Install a matching Windows x64 Social SDK build."); }
    }

    private static Runtime LoadRuntime(string directory)
    {
        var native = NativeLibrary.Load(Path.Combine(directory, "discord_partner_sdk.dll"), typeof(DiscordSdkClient).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        try
        {
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "Discord.Sdk.dll"));
            var pump = Marshal.GetDelegateForFunctionPointer<PumpCallback>(NativeLibrary.GetExport(native, "Discord_RunCallbacks"));
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
                name.Contains("discord_partner_sdk", StringComparison.OrdinalIgnoreCase) ? native : IntPtr.Zero);
            return new(assembly, native, () => pump());
        }
        catch { NativeLibrary.Free(native); throw; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PumpCallback();

    public async Task<DiscordSession> AuthorizeAsync(ulong applicationId, CancellationToken token)
    {
        _applicationId = applicationId;
        Invoke(_client, "SetApplicationId", applicationId);
        var verifier = Invoke(_client, "CreateAuthorizationCodeVerifier")!;
        var args = New("AuthorizationArgs");
        var challenge = Invoke(verifier, "Challenge");
        try
        {
            var verifierText = (string)Invoke(verifier, "Verifier")!;
            Invoke(args, "SetClientId", applicationId);
            Invoke(args, "SetScopes", Invoke(null, "GetDefaultCommunicationScopes"));
            Invoke(args, "SetCodeChallenge", challenge);
            using var abort = token.Register(() => _pump.Dispatcher.BeginInvoke(() =>
            { if (!_disposed) Invoke(_client, "AbortAuthorize"); }));
            DiscordAuthorizationCode authorization;
            try
            {
                authorization = await RequestAsync("Authorize", [args], values =>
                {
                    CheckResult(values[0]);
                    return new DiscordAuthorizationCode((string)values[1]!, (string)values[2]!);
                }, token);
            }
            catch (DiscordDesktopApprovalException)
            {
                Invoke(_client, "AbortAuthorize");
                authorization = await new DiscordBrowserAuthorization().AuthorizeAsync(applicationId,
                    (string)Invoke(null, "GetDefaultCommunicationScopes")!, (string)Call(challenge!, "Challenge")!, token);
            }
            return await RequestAsync("GetToken", [applicationId, authorization.Code, verifierText, authorization.Redirect],
                values => ReadSession(applicationId, values), token);
        }
        finally { DisposeObject(args); DisposeObject(challenge); DisposeObject(verifier); }
    }

    public Task<DiscordSession> RefreshAsync(DiscordSession session, CancellationToken token) =>
        RequestAsync("RefreshToken", [session.ApplicationId, session.RefreshToken],
            values => ReadSession(session.ApplicationId, values), token);

    public async Task ConnectAsync(ulong applicationId, string accessToken, CancellationToken token)
    {
        _applicationId = applicationId;
        Invoke(_client, "SetApplicationId", applicationId);
        // Register only the running production app, never the unit-test host or a
        // path from an invitation. Discord uses this to open Steamy on desktop.
        if (App.IsLaunched && Environment.ProcessPath is { } executable && Path.GetFileName(executable).Equals("Steamy.exe", StringComparison.OrdinalIgnoreCase))
            Invoke(_client, "RegisterLaunchCommand", applicationId, "\"" + executable + "\" --discord-join");
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var bearer = Enum.Parse(Type("AuthorizationTokenType"), "Bearer");
        await ResultAsync("UpdateToken", [bearer, accessToken], token);
        if (_connected) { RefreshSnapshot(); return; }
        Invoke(_client, "Connect");
        await _ready.Task.WaitAsync(token);
        if (Snapshot.Profile is null) throw new DiscordSocialException("Discord didn't return your profile. Reconnect and try again.");
    }

    public Task RevokeAsync(DiscordSession session, CancellationToken token) =>
        ResultAsync("RevokeToken", [session.ApplicationId, session.RefreshToken], token);

    public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token) =>
        DiscordConfiguration.TryApplicationId(usernameOrId, out var id)
            ? ResultAsync("SendDiscordFriendRequestById", [id], token)
            : ResultAsync("SendDiscordFriendRequest", [usernameOrId], token);

    public Task UpdateFriendAsync(ulong userId, DiscordFriendAction action, CancellationToken token) =>
        ResultAsync(action switch
        {
            DiscordFriendAction.Accept => "AcceptDiscordFriendRequest",
            DiscordFriendAction.Reject => "RejectDiscordFriendRequest",
            DiscordFriendAction.Cancel => "CancelDiscordFriendRequest",
            DiscordFriendAction.Remove => "RemoveDiscordFriend",
            DiscordFriendAction.Block => "BlockUser",
            DiscordFriendAction.Unblock => "UnblockUser",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        }, [userId], token);

    private Task<bool> ResultAsync(string method, object?[] arguments, CancellationToken token) =>
        RequestAsync(method, arguments, values => { CheckResult(values[0]); RefreshSnapshot(); return true; }, token);

    private static DiscordSession ReadSession(ulong applicationId, object?[] values)
    {
        CheckResult(values[0]);
        var accessToken = (string)values[1]!;
        var refreshToken = (string)values[2]!;
        var expiresIn = Convert.ToInt32(values[4]);
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken)
            || values[3]?.ToString() != "Bearer" || expiresIn <= 0)
            throw new DiscordAuthorizationException("Discord didn't return a valid session. Please sign in again.");
        return new(applicationId, accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn), (string)values[5]!);
    }

    private static void CheckResult(object? result, string operation = "request")
    {
        if (result is null) throw new DiscordSocialException("Discord didn't return a result. Please try again.");
        if ((bool)Call(result, "Successful")!) return;
        var error = Call(result, "Type")?.ToString();
        var status = Convert.ToInt32(Call(result, "Status"));
        var code = Convert.ToInt32(Call(result, "ErrorCode"));
        // Only a whitelisted OAuth error identifier is extracted from the response.
        // Tokens, descriptions and the complete response never enter a message or log.
        var failure = DiscordSdkErrors.Create(operation, error ?? "", status, code, Call(result, "ResponseBody") as string);
        // The formatter uses fixed operation labels and allowlisted error names only.
        // Never log the response body, arguments, tokens, lobby secrets or message text.
        try { App.Services?.GetService<ILoggingService>()?.Add(Models.LogLevel.Warning, "Discord", failure.Message); } catch { }
        throw failure;
    }

    private void RefreshSnapshot()
    {
        if (!_connected || _disposed) return;
        var userMethod = _client.GetType().GetMethod("GetCurrentUserV2") is null ? "GetCurrentUser" : "GetCurrentUserV2";
        var current = Invoke(_client, userMethod);
        DiscordProfile? profile;
        try { profile = Profile(current); }
        finally { DisposeObject(current); }
        var friends = new List<DiscordFriend>();
        foreach (var relation in (IEnumerable)Invoke(_client, "GetRelationships")!)
        {
            var user = Call(relation, "User");
            try
            {
                if (Profile(user) is not { } friendProfile) continue;
                if (friendProfile.Id == profile?.Id) continue;
                if (!Enum.TryParse<DiscordRelationshipKind>(Call(relation, "DiscordRelationshipType")?.ToString(), out var kind)) continue;
                var status = Call(user!, "Status")?.ToString() switch
                { "Online" => "Online", "Idle" => "Idle", "Dnd" or "DoNotDisturb" => "Do not disturb", _ => "Offline" };
                friends.Add(new(friendProfile, kind, status));
            }
            finally { DisposeObject(user); DisposeObject(relation); }
        }
        Snapshot = new(true, profile, friends.OrderByDescending(f => f.IsOnline).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DiscordProfile? Profile(object? user)
    {
        if (user is null) return null;
        var id = Convert.ToUInt64(Call(user, "Id"));
        if (id == 0) return null;
        var png = Enum.Parse(Type("UserHandle+AvatarType"), "Png");
        var avatar = Call(user, "AvatarUrl", png, png) as string;
        if (!Uri.TryCreate(avatar, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "cdn.discordapp.com") avatar = null;
        return new(id, (string)Call(user, "Username")!, (string)Call(user, "DisplayName")!, avatar);
    }

    private async Task<T> RequestAsync<T>(string name, object?[] arguments, Func<object?[], T> read, CancellationToken token, bool hasResult = true)
    {
        token.ThrowIfCancellationRequested();
        var method = Method(_client.GetType(), name, arguments.Length + 1);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = token.Register(() => completion.TrySetCanceled(token));
        Delegate? callback = null;
        callback = MakeCallback(method.GetParameters()[^1].ParameterType, values =>
        {
            try
            {
                if (!completion.Task.IsCompleted)
                {
                    if (hasResult) CheckResult(values[0], name);
                    completion.TrySetResult(read(values));
                }
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { _callbacks.Remove(callback!); }
        });
        _callbacks.Add(callback);
        try { method.Invoke(_client, [.. arguments, callback]); }
        catch { _callbacks.Remove(callback); throw; }
        return await completion.Task;
    }

    private void SetCallback(string method, Action<object?[]> action)
    {
        var info = Method(_client.GetType(), method, 1);
        var callback = MakeCallback(info.GetParameters()[0].ParameterType, values =>
        {
            try { if (!_disposed) action(values); }
            catch { _ready?.TrySetException(new DiscordSocialException("Discord support couldn't read its current state. Please reconnect.")); }
        });
        _callbacks.Add(callback);
        info.Invoke(_client, [callback]);
    }

    private static Delegate MakeCallback(Type type, Action<object?[]> action)
    {
        var parameters = type.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        return Expression.Lambda(type, Expression.Invoke(Expression.Constant(action),
            Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))))), parameters).Compile();
    }
    private Type Type(string name) => _assembly.GetType("Discord.Sdk." + name, throwOnError: true)!;
    private object New(string name) => Activator.CreateInstance(Type(name))!;
    private object? Invoke(object? target, string name, params object?[] arguments) =>
        Method(target?.GetType() ?? Type("Client"), name, arguments.Length).Invoke(target, arguments);
    private static object? Call(object target, string name, params object?[] arguments) => Method(target.GetType(), name, arguments.Length).Invoke(target, arguments);
    private static MethodInfo Method(Type type, string name, int count) => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count) ?? throw new MissingMethodException(type.FullName, name);
    private static void DisposeObject(object? value) { if (value is IDisposable disposable) disposable.Dispose(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pump.Stop();
        DisposeVoice(); DisposeCommunication();
        try { Invoke(_client, "AbortAuthorize"); Invoke(_client, "Disconnect"); }
        finally { DisposeObject(_client); _callbacks.Clear(); Snapshot = DiscordSnapshot.Disconnected; }
    }
}
