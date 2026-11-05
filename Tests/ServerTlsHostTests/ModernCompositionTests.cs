using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Authentication;
using Autofac;
using Connections;
using PhinixServer;
using ServerTransport;
using UserManagement;

internal static partial class Program
{
    static async Task ModernCompositionTests(string parent, X509Certificate2 root, X509Certificate2 certificate, string certificateFile, string keyFile)
    {
        string directory = Path.Combine(parent, "modern-composition"); Directory.CreateDirectory(directory);
        var config = new Config { Address = IPAddress.Loopback, Port = 0, MaxConnections = 4,
            CredentialDatabasePath = Path.Combine(directory, "credentials"), UserDatabasePath = Path.Combine(directory, "users"),
            Tls = new TlsServerSettings { CertificateFile = certificateFile, PrivateKeyFile = keyFile, MaximumHandshakes = 2 },
            ModernIdentity = new ModernIdentitySettings { DatabaseFile = Path.Combine(directory, "identity.sqlite3"), InitializeNew = true } };
        var build = typeof(Config).Assembly.GetType("PhinixServer.ServerComposition")!.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!;
        string stable;
        using (var container = (IContainer)build.Invoke(null, new object[] { config })!)
        using (var scope = container.BeginLifetimeScope())
        {
            var transport = scope.Resolve<IServerTransport>(); var facade = scope.Resolve<ServerAuthenticator>(); var users = scope.Resolve<ServerUserManager>();
            facade.Load(config.CredentialDatabasePath); users.Load(config.UserDatabasePath);
            var endpoint = scope.Resolve<PasswordlessIdentityEndpoint>(); var authority = scope.Resolve<PasswordlessIdentityStore>(); stable = authority.ServerId;
            Check(ReferenceEquals(endpoint, scope.Resolve<PasswordlessIdentityEndpoint>()) && facade.UsesModernIdentity, "Modern identity was not composed with a scoped endpoint");
            bool duplicateRefused = false;
            try { using var duplicate = new PasswordlessIdentityEndpoint(transport, facade, authority, users.IdentityAdapter.ResolveProof, config.ServerName); }
            catch (PacketHandlerAlreadyRegisteredException) { duplicateRefused = true; }
            Check(duplicateRefused, "Duplicate identity module took over the existing authentication handler");
            transport.Start();
            using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, transport.LocalPort, Deadline.Token);
            await using var channel = await TlsPacketChannel.ConnectAsync(socket.GetStream(), "localhost", SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()), root, X509RevocationMode.NoCheck, cancellation: Deadline.Token);
            var hello = TlsModulePacket.Decode((await channel.ReadAsync(Deadline.Token))!);
            Check(hello.Module == IdentityWire.Module && IdentityWire.Decode(hello.Payload).ServerId == stable, "Production composition sent the previous authentication greeting");
            // Independent malformed JSON oracle; no product serializer generates this expectation.
            byte[] invalid = Encoding.UTF8.GetBytes("{\"Version\":1,\"Version\":1,\"Kind\":\"Enroll\"}");
            await channel.WriteAsync(TlsModulePacket.Encode(IdentityWire.Module, invalid, 4096), Deadline.Token);
            Check(await channel.ReadAsync(Deadline.Token) == null, "Duplicate bootstrap fields were not refused");
            transport.Stop();
        }
        Check(IdentityAuthorityBootstrap.Open(config.ModernIdentity.DatabaseFile, false).ServerId == stable, "Startup recreated the server identity");
        var modern = config.ModernIdentity; config.ModernIdentity = null;
        bool refused = false; try { using var _ = (IContainer)build.Invoke(null, new object[] { config })!; } catch (TargetInvocationException) { refused = true; }
        Check(refused, "Disabling modern identity silently reactivated the previous credential source"); config.ModernIdentity = modern;
        File.Move(modern.DatabaseFile, modern.DatabaseFile + ".saved");
        refused = false; try { IdentityAuthorityBootstrap.Open(modern.DatabaseFile, true); } catch (IOException) { refused = true; }
        Check(refused && !File.Exists(modern.DatabaseFile), "Missing initialized identity database was silently recreated");
        File.Move(modern.DatabaseFile + ".saved", modern.DatabaseFile);
        File.Move(config.UserDatabasePath, config.UserDatabasePath + ".saved"); refused = false;
        try { using var _ = (IContainer)build.Invoke(null, new object[] { config })!; } catch (TargetInvocationException) { refused = true; }
        Check(refused && !File.Exists(config.UserDatabasePath), "Missing player snapshot was silently recreated beside an existing identity authority");
        File.Move(config.UserDatabasePath + ".saved", config.UserDatabasePath);
        config.Tls = null; refused = false; try { config.ValidateCore(); } catch (System.Runtime.Serialization.SerializationException) { refused = true; }
        Check(refused, "Modern identity allowed UDP configuration");
        await SessionReplacementRace();
        foreach (string malformed in new[] { "{}", "null", "{\"Version\":2,\"Kind\":\"Hello\"}", "{\"Version\":1,\"Kind\":\"Hello\",\"Unexpected\":true}" })
        {
            refused = false; try { IdentityWire.Decode(Encoding.UTF8.GetBytes(malformed)); } catch (InvalidDataException) { refused = true; }
            Check(refused, "Invalid identity envelope accepted");
        }
    }
    static async Task SessionReplacementRace()
    {
        using var observed = new System.Threading.ManualResetEventSlim(); using var release = new System.Threading.ManualResetEventSlim();
        string current = "first"; int pause = 0;
        var registry = new ServerRuntime.ServerSessionRegistry((connection, principal, session) => {
            if (session == "first" && System.Threading.Volatile.Read(ref pause) == 1) { observed.Set(); if (!release.Wait(5000)) throw new TimeoutException(); }
            return session == System.Threading.Volatile.Read(ref current);
        });
        Check(registry.TryBind("connection", "principal", "first", 2, Array.Empty<string>(), Array.Empty<string>(), out var initial, out _) && registry.MarkReady(initial), "Race fixture did not reach Ready");
        System.Threading.Volatile.Write(ref pause, 1);
        var stale = Task.Run(() => registry.TryGetReady("connection", out _));
        try
        {
            Check(observed.Wait(5000), "Stale reader did not reach its deterministic barrier");
            System.Threading.Volatile.Write(ref current, "second");
            Check(registry.ReplaceSession("connection", "first", "second"), "Trusted session replacement was refused");
        }
        finally { release.Set(); }
        await stale;
        Check(registry.TryGetReady("connection", out var ready) && ready.SessionId == "second", "An old session reader poisoned the replacement Ready state");
    }

}
