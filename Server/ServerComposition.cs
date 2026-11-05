using System.Net;
using Authentication;
using Autofac;
using Connections;
using UserManagement;

namespace PhinixServer
{
    // Host composition only. Plugins keep the common registry/context contract, not Autofac dependencies.
    internal static class ServerComposition
    {
        internal static IContainer Build(Config config)
        {
            config.ValidateCore();
            // The pinned LiteNetLib Start API binds wildcard sockets, not a supplied IP.
            // Refuse restrictive configuration instead of silently exposing a wider interface.
            if (config.Tls == null && !IPAddress.Any.Equals(config.Address))
                throw new System.NotSupportedException("Pinned transport supports wildcard binding only.");
            if (config.ModernIdentity == null && System.IO.File.Exists(config.CredentialDatabasePath + ".modern-identity-required"))
                throw new System.InvalidOperationException("Modern identity authority exists; disabling it would reactivate retired identity sources.");
            if (config.ModernIdentity != null && (System.IO.File.Exists(config.ModernIdentity.DatabaseFile) || System.IO.File.Exists(config.ModernIdentity.DatabaseFile + ".identity-initialized") || System.IO.File.Exists(config.CredentialDatabasePath + ".modern-identity-required")) &&
                (!System.IO.File.Exists(config.CredentialDatabasePath) || !System.IO.File.Exists(config.UserDatabasePath)))
                throw new System.IO.IOException("Expected player or migration credential snapshot is missing; refusing an empty identity mapping.");
            var builder = new ContainerBuilder();
            builder.Register(_ => new ServerRuntime.ServerWorkQueue(reportFailure: type =>
                System.Console.Error.WriteLine("Deferred server work failed: " + type)))
                .AsSelf().As<ServerRuntime.IServerWorkQueue>().InstancePerLifetimeScope();
            builder.RegisterInstance(config).ExternallyOwned();
            if (config.Tls == null)
                builder.Register(c => new NetServer(new IPEndPoint(config.Address, config.Port), config.MaxConnections))
                    .AsSelf().As<IServerTransport>().InstancePerLifetimeScope();
            else
                builder.Register(c => {
                    var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(
                        config.Tls.CertificateFile, config.Tls.PrivateKeyFile);
                    try { return new TlsNetServer(new IPEndPoint(config.Address, config.Port), cert, System.Math.Min(config.MaxConnections, config.Tls.ConnectionLimit),
                        config.Tls.MaximumHandshakes, config.Tls.MaximumPacketBytes, ownsCertificate: true); }
                    catch { cert.Dispose(); throw; }
                }).As<IServerTransport>().InstancePerLifetimeScope();
            builder.Register(c => new ServerAuthenticator(c.Resolve<IServerTransport>(), config.ServerName, config.ServerDescription, config.AuthType))
                .OnRelease(auth => auth.Stop()).InstancePerLifetimeScope();
            builder.Register(c => new ServerUserManager(c.Resolve<IServerTransport>(), c.Resolve<ServerAuthenticator>(), config.MaxDisplayNameLength))
                .InstancePerLifetimeScope();
            builder.Register(c => new ServerStorage.SqliteOperationJournalFactory(System.IO.Path.Combine("framework-journals", "server")))
                .As<ServerStorage.IOperationJournalFactory>().InstancePerLifetimeScope();
            if (config.ModernIdentity != null)
            {
                builder.Register(_ => {
                    var authority = IdentityAuthorityBootstrap.Open(config.ModernIdentity.DatabaseFile, config.ModernIdentity.InitializeNew);
                    IdentityAuthorityBootstrap.PinMode(config.CredentialDatabasePath + ".modern-identity-required", authority.ServerId);
                    return authority;
                }).InstancePerLifetimeScope();
                builder.Register(c => new PasswordlessIdentityEndpoint(c.Resolve<IServerTransport>(), c.Resolve<ServerAuthenticator>(),
                    c.Resolve<PasswordlessIdentityStore>(), c.Resolve<ServerUserManager>().IdentityAdapter.ResolveProof, config.ServerName, config.ModernIdentity.RegistrationPerMinute))
                    .InstancePerLifetimeScope();
            }
            return builder.Build();
        }
    }
}
