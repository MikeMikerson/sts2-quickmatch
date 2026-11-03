using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SteamKit2;

namespace QuickMatchAndroid.Launcher;

/// <summary>
/// A Steam connection of the Android StS2 Launcher's own kind, for lobbies. The launcher signs in on
/// its own screens before the game starts and keeps the account name and refresh token in its
/// Keystore-protected store; it publishes no API for mods, so this class reaches the store and the
/// launcher's SteamConnection by reflection. The token goes from the store straight into the
/// connection's constructor and is never kept or logged here. The connection is separate from the
/// launcher's cloud-save one, so neither one's idle disconnect affects the other.
/// This is the only file that knows the launcher's internals (checked against launcher 0.3.17).
/// </summary>
internal sealed class LauncherSession : ISteamSession
{
    internal const string ConnectionTypeName = "STS2Mobile.Steam.Connection.SteamConnection, STS2Mobile.Steam";
    internal const string LoggerInterfaceName = "STS2Mobile.Steam.Abstractions.ILogger, STS2Mobile.Steam";
    internal const string CredentialStoreTypeName = "STS2Mobile.Services.AndroidSteamCredentialStore, STS2Mobile";
    internal const string LoggerTypeName = "STS2Mobile.Services.AndroidLogger, STS2Mobile";

    /// <summary>The launcher's own value: its connection closes after 30 idle seconds.</summary>
    private const int IdleTimeoutMs = 30_000;

    private readonly object _connection;
    private readonly PropertyInfo _state;
    private readonly PropertyInfo _apps;
    private readonly MethodInfo _suspendIdle;
    private readonly MethodInfo _resumeIdle;

    private LauncherSession(object connection, SteamClient client, PropertyInfo state, PropertyInfo apps, MethodInfo suspendIdle, MethodInfo resumeIdle)
    {
        _connection = connection;
        Client = client;
        _state = state;
        _apps = apps;
        _suspendIdle = suspendIdle;
        _resumeIdle = resumeIdle;
    }

    public SteamClient Client { get; }

    public bool IsConnected => _state.GetValue(_connection)?.ToString() == "Connected";

    /// <summary>The launcher's connection connects and signs in when a handler is first asked for.</summary>
    public void Connect() => Call(() => _apps.GetValue(_connection));

    public void HoldOpen() => Call(() => _suspendIdle.Invoke(_connection, null));

    public void Release() => Call(() => _resumeIdle.Invoke(_connection, null));

    /// <summary>True when the launcher's Steam classes are loaded, that is, the game runs inside it.</summary>
    public static bool InLauncher(Func<string, Type?> findType) => findType(ConnectionTypeName) != null;

    /// <summary>
    /// The lobby connection, or null when the launcher has no saved sign-in or a member this needs is
    /// missing (another launcher version); <paramref name="report"/> then says which.
    /// </summary>
    public static LauncherSession? Create(Func<string, Type?> findType, Action<string> report)
    {
        try
        {
            Type? storeType = findType(CredentialStoreTypeName);
            Type? loggerType = findType(LoggerTypeName);
            Type? loggerInterface = findType(LoggerInterfaceName);
            Type? connectionType = findType(ConnectionTypeName);
            if (storeType == null || loggerType == null || loggerInterface == null || connectionType == null)
            {
                report("launcher types not found: " + Missing(
                    (storeType, CredentialStoreTypeName), (loggerType, LoggerTypeName),
                    (loggerInterface, LoggerInterfaceName), (connectionType, ConnectionTypeName)));
                return null;
            }

            const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public;
            MethodInfo? load = storeType.GetMethod("Load", Members, Type.EmptyTypes);
            PropertyInfo? hasCredentials = storeType.GetProperty("HasCredentials", Members);
            PropertyInfo? accountName = storeType.GetProperty("AccountName", Members);
            PropertyInfo? refreshToken = storeType.GetProperty("RefreshToken", Members);
            ConstructorInfo? connect = connectionType.GetConstructor(Members, new[] { typeof(string), typeof(string), loggerInterface, typeof(int) });
            PropertyInfo? client = connectionType.GetProperty("Client", Members);
            PropertyInfo? state = connectionType.GetProperty("State", Members);
            PropertyInfo? apps = connectionType.GetProperty("Apps", Members);
            MethodInfo? suspendIdle = connectionType.GetMethod("SuspendIdleTimeout", Members, Type.EmptyTypes);
            MethodInfo? resumeIdle = connectionType.GetMethod("ResumeIdleTimeout", Members, Type.EmptyTypes);
            if (load == null || hasCredentials == null || accountName == null || refreshToken == null || connect == null
                || client == null || state == null || apps == null || suspendIdle == null || resumeIdle == null)
            {
                report("launcher members not found: " + Missing(
                    (load, "credential store Load()"), (hasCredentials, "HasCredentials"), (accountName, "AccountName"),
                    (refreshToken, "RefreshToken"), (connect, "SteamConnection(string, string, ILogger, int)"),
                    (client, "Client"), (state, "State"), (apps, "Apps"),
                    (suspendIdle, "SuspendIdleTimeout()"), (resumeIdle, "ResumeIdleTimeout()")));
                return null;
            }

            object store = Activator.CreateInstance(storeType, nonPublic: true)!;
            Call(() => load.Invoke(store, null));
            string? account = accountName.GetValue(store) as string;
            if (hasCredentials.GetValue(store) is not true || string.IsNullOrEmpty(account))
            {
                report("the launcher has no saved Steam sign-in");
                return null;
            }

            object logger = Activator.CreateInstance(loggerType, nonPublic: true)!;
            object connection = Call(() => connect.Invoke(new[] { account, refreshToken.GetValue(store), logger, IdleTimeoutMs }))!;
            if (client.GetValue(connection) is not SteamClient steamClient)
            {
                report("the launcher's SteamConnection.Client is not this mod's SteamKit2 SteamClient");
                return null;
            }
            return new LauncherSession(connection, steamClient, state, apps, suspendIdle, resumeIdle);
        }
        catch (Exception e)
        {
            report($"could not use the launcher's Steam sign-in: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static string Missing(params (object? Found, string Name)[] items)
    {
        return string.Join(", ", Array.FindAll(items, item => item.Found == null).Select(item => item.Name));
    }

    /// <summary>Reflection wraps what the launcher throws; this passes on the launcher's own exception.</summary>
    private static object? Call(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
