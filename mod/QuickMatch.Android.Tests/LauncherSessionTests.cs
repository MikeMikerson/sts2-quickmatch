using QuickMatchAndroid.Launcher;
using SteamKit2;
using Xunit;

namespace QuickMatch.Android.Tests;

/// <summary>
/// The bridge against stand-ins with the shape of launcher 0.3.17's classes (read from its APK):
/// AndroidSteamCredentialStore (internal, Load, HasCredentials, AccountName, RefreshToken),
/// AndroidLogger (internal), ILogger, and SteamConnection (ctor, Client, State, Apps, idle hold).
/// </summary>
public class LauncherSessionTests
{
    private const string Token = "eyJ.refresh-token-that-must-never-be-logged";

    private readonly List<string> _reports = new();

    public LauncherSessionTests()
    {
        FakeStore.Reset(account: "player", token: Token);
        FakeConnection.Created.Clear();
    }

    private static Func<string, Type?> Launcher(Type? connection = null) => name => name switch
    {
        LauncherSession.CredentialStoreTypeName => typeof(FakeStore),
        LauncherSession.LoggerTypeName => typeof(FakeLogger),
        LauncherSession.LoggerInterfaceName => typeof(IFakeLogger),
        LauncherSession.ConnectionTypeName => connection ?? typeof(FakeConnection),
        _ => null,
    };

    [Fact]
    public void SignInComesFromTheLauncherStoreAndConnectsOnlyWhenAsked()
    {
        LauncherSession? session = LauncherSession.Create(Launcher(), _reports.Add);

        Assert.NotNull(session);
        Assert.Empty(_reports);
        FakeConnection connection = Assert.Single(FakeConnection.Created);
        Assert.Equal(("player", Token, 30_000), (connection.Account, connection.Token, connection.IdleTimeoutMs));
        Assert.IsType<FakeLogger>(connection.Logger);
        Assert.Same(connection.Client, session!.Client);
        Assert.Equal(0, connection.Connects);
        Assert.False(session.IsConnected);

        session.Connect();
        Assert.Equal(1, connection.Connects);
        Assert.True(session.IsConnected);

        session.HoldOpen();
        session.HoldOpen();
        Assert.Equal(2, connection.Holds);
        session.Release();
        Assert.Equal(1, connection.Holds);
    }

    [Fact]
    public void NoSavedSignInMeansNoSession()
    {
        FakeStore.Reset(account: null, token: null);

        Assert.Null(LauncherSession.Create(Launcher(), _reports.Add));
        Assert.Equal(new[] { "the launcher has no saved Steam sign-in" }, _reports);
        Assert.Empty(FakeConnection.Created);
    }

    [Fact]
    public void AnotherLauncherVersionIsNamedAndRefused()
    {
        Assert.Null(LauncherSession.Create(Launcher(typeof(ConnectionWithoutIdleHold)), _reports.Add));
        string report = Assert.Single(_reports);
        Assert.Contains("SuspendIdleTimeout()", report);
        Assert.Contains("ResumeIdleTimeout()", report);

        _reports.Clear();
        Assert.Null(LauncherSession.Create(_ => null, _reports.Add));
        Assert.Contains(LauncherSession.ConnectionTypeName, Assert.Single(_reports));
    }

    [Fact]
    public void TheTokenNeverReachesAReport()
    {
        FakeStore.LoadFails = true;
        Assert.Null(LauncherSession.Create(Launcher(), _reports.Add));
        FakeStore.LoadFails = false;
        Assert.Null(LauncherSession.Create(Launcher(typeof(ThrowingConnection)), _reports.Add));

        Assert.Equal(2, _reports.Count);
        Assert.All(_reports, report => Assert.DoesNotContain(Token, report));
        Assert.Contains("keystore unavailable", _reports[0]);
    }

    [Fact]
    public void ConnectPassesOnTheLaunchersOwnException()
    {
        LauncherSession session = LauncherSession.Create(Launcher(), _reports.Add)!;
        FakeConnection.Created[0].FailConnect = true;

        Assert.Throws<TimeoutException>(session.Connect);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public void OnlyTheLauncherHasItsSteamClasses()
    {
        Assert.True(LauncherSession.InLauncher(Launcher()));
        Assert.False(LauncherSession.InLauncher(_ => null));
    }

    public interface IFakeLogger
    {
    }

    internal sealed class FakeLogger : IFakeLogger
    {
    }

    public enum FakeState
    {
        Idle,
        Connecting,
        Connected,
    }

    internal sealed class FakeStore
    {
        private static string? _account;
        private static string? _token;
        private bool _loaded;

        public static bool LoadFails { get; set; }

        public static void Reset(string? account, string? token)
        {
            _account = account;
            _token = token;
            LoadFails = false;
        }

        public bool HasCredentials => _loaded && _account != null && _token != null;

        public string? AccountName => _loaded ? _account : null;

        public string? RefreshToken => _loaded ? _token : null;

        public void Load()
        {
            if (LoadFails)
            {
                throw new InvalidOperationException("keystore unavailable");
            }
            _loaded = true;
        }
    }

    public class FakeConnection
    {
        public static readonly List<FakeConnection> Created = new();

        public FakeConnection(string accountName, string refreshToken, IFakeLogger logger, int idleTimeoutMs)
        {
            Account = accountName;
            Token = refreshToken;
            Logger = logger;
            IdleTimeoutMs = idleTimeoutMs;
            Created.Add(this);
        }

        public string Account { get; }

        public string Token { get; }

        public IFakeLogger Logger { get; }

        public int IdleTimeoutMs { get; }

        public int Connects { get; private set; }

        public int Holds { get; private set; }

        public bool FailConnect { get; set; }

        public SteamClient Client { get; } = new();

        public FakeState State { get; private set; }

        public object Apps
        {
            get
            {
                Connects++;
                if (FailConnect)
                {
                    throw new TimeoutException("Steam connection timed out");
                }
                State = FakeState.Connected;
                return new object();
            }
        }

        public void SuspendIdleTimeout() => Holds++;

        public void ResumeIdleTimeout() => Holds--;
    }

    public sealed class ThrowingConnection : FakeConnection
    {
        public ThrowingConnection(string accountName, string refreshToken, IFakeLogger logger, int idleTimeoutMs)
            : base(accountName, refreshToken, logger, idleTimeoutMs)
        {
            throw new ArgumentException("bad account");
        }
    }

    public sealed class ConnectionWithoutIdleHold
    {
        public ConnectionWithoutIdleHold(string accountName, string refreshToken, IFakeLogger logger, int idleTimeoutMs)
        {
        }

        public SteamClient Client { get; } = new();

        public FakeState State => FakeState.Idle;

        public object Apps => new();
    }
}
