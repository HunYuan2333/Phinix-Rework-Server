using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using Autofac;
using Authentication;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using PhinixServer;
using PhinixServer.Framework;
using Utils.Framework;
using ServerTransport;
using UserManagement;
using Utils;

internal static partial class Program
{
    static int checks;
    static void Check(bool ok,string reason) { checks++;if(!ok)throw new Exception(reason); }
    static readonly CancellationTokenSource Deadline=new(TimeSpan.FromSeconds(40));
    static async Task Wait(Func<bool> ready) { while(!ready())await Task.Delay(10,Deadline.Token); }
    static X509Certificate2 Root()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN=synthetic-ca",key,HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign,true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(2));
    }
    static X509Certificate2 Leaf(X509Certificate2 root)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
        using var publicOnly=request.Create(root,DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));
        return publicOnly.CopyWithPrivateKey(key);
    }
    static async Task<byte[]> Receive(TlsPacketChannel channel,string module)
    {
        while(true)
        {
            var packet=TlsModulePacket.Decode((await channel.ReadAsync(Deadline.Token))!);
            if(packet.Module==module)return packet.Payload;
        }
    }
    static Task Send(TlsPacketChannel channel,string module,IMessage packet) => channel.WriteAsync(TlsModulePacket.Encode(module,ProtobufPacketHelper.Pack(packet).ToByteArray(),4096),Deadline.Token).AsTask();
    static async Task<string> Login(IServerTransport transport,X509Certificate2 root,X509Certificate2 cert,string key)
    {
        using var socket=new TcpClient();await socket.ConnectAsync(IPAddress.Loopback,transport.LocalPort,Deadline.Token);
        await using var channel=await TlsPacketChannel.ConnectAsync(socket.GetStream(),"localhost",SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()),root,
            X509RevocationMode.NoCheck,maximum:4096,frameTimeout:TimeSpan.FromSeconds(4),cancellation:Deadline.Token);
        var hello=Any.Parser.ParseFrom(await Receive(channel,"Authentication")).Unpack<HelloPacket>();
        await Send(channel,"Authentication",new AuthenticatePacket{SessionId=hello.SessionId,AuthType=AuthTypes.ClientKey,Username=key});
        Check(Any.Parser.ParseFrom(await Receive(channel,"Authentication")).Unpack<AuthResponsePacket>().Success,"Real authenticator refused synthetic login over TLS");
        await Send(channel,"UserManagement",new LoginPacket{SessionId=hello.SessionId,DisplayName="synthetic-tls",AcceptingTrades=true});
        var login=Any.Parser.ParseFrom(await Receive(channel,"UserManagement")).Unpack<LoginResponsePacket>();
        Check(login.Success && !string.IsNullOrWhiteSpace(login.Uuid),"Real user login failed over TLS");
        var negotiation=new FrameworkPacket{Kind=FrameworkProtocol.KindHello,MessageId=Guid.NewGuid().ToString(),Version=2,
            SessionId=hello.SessionId,SenderUuid=login.Uuid,
            PayloadJson=FrameworkSerialization.SerializePayload(new FrameworkCapabilitiesPayload{Capabilities=new List<string>()})};
        await channel.WriteAsync(TlsModulePacket.Encode(FrameworkProtocol.ModuleName,FrameworkSerialization.SerializePacket(negotiation),4096),Deadline.Token);
        var negotiated=FrameworkSerialization.DeserializePacket(await Receive(channel,FrameworkProtocol.ModuleName));
        Check(negotiated.Kind==FrameworkProtocol.KindCapabilities,"Real Framework pipeline failed TLS negotiation");
        // Independent literal module body (ushort 4, ASCII test, opaque 17/0/255), no product encoder oracle.
        await channel.WriteAsync(new byte[]{0,4,116,101,115,116,17,0,255},Deadline.Token);
        var echoed=await Receive(channel,"test");Check(echoed.SequenceEqual(new byte[]{17,0,255}),"Opaque plugin payload changed in TLS adapter");
        return login.Uuid;
    }
    static async Task Main()
    {
        string rootPath=Path.Combine(Path.GetTempPath(),"phinix-tls-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(rootPath);
        using var root=Root();using var cert=Leaf(root);
        try
        {
            await ObserverReliabilityTests(root,cert);
            await SessionTests(rootPath,root,cert);
            string certificate=Path.Combine(rootPath,"cert.pem"),privateKey=Path.Combine(rootPath,"key.pem");
            File.WriteAllText(certificate,cert.ExportCertificatePem());
            using(var key=cert.GetECDsaPrivateKey())File.WriteAllText(privateKey,key!.ExportPkcs8PrivateKeyPem());
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(privateKey,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            await ModernCompositionTests(rootPath,root,cert,certificate,privateKey);
            var config=new Config{Address=IPAddress.Loopback,Port=0,MaxConnections=4,Tls=new TlsServerSettings{CertificateFile=certificate,PrivateKeyFile=privateKey,MaximumHandshakes=2,MaximumPacketBytes=4096}};
            var composition=typeof(Config).Assembly.GetType("PhinixServer.ServerComposition")!.GetMethod("Build",BindingFlags.Static|BindingFlags.NonPublic)!;
            using var container=(IContainer)composition.Invoke(null,new object[]{config})!;
            using var scope=container.BeginLifetimeScope();var transport=scope.Resolve<IServerTransport>();
            Check(transport is TlsNetServer && !scope.IsRegistered<NetServer>(),"Explicit TLS config must resolve TLS only, without UDP fallback");
            var auth=scope.Resolve<ServerAuthenticator>();var users=scope.Resolve<ServerUserManager>();
            auth.Load(Path.Combine(rootPath,"credentials"));users.Load(Path.Combine(rootPath,"users"));
            var context=new ExtensionHostContext{HostKind="server",StorageProvider=new FileSystemExtensionStorageProvider(Path.Combine(rootPath,"plugins"))};
            context.AddService<IServerUserManager>(users);context.AddService<IExtensionConfigProvider>(config);
            var framework=new PhinixFrameworkServer(transport,auth,users,context);
            var callbackThreads=new System.Collections.Concurrent.ConcurrentBag<int>();int closed=0;
            transport.OnConnectionEstablished+=(_,_)=>callbackThreads.Add(Environment.CurrentManagedThreadId);
            transport.OnConnectionClosed+=(_,_)=>{callbackThreads.Add(Environment.CurrentManagedThreadId);Interlocked.Increment(ref closed);};
            transport.RegisterPacketHandler("test",(module,id,payload)=>{
                callbackThreads.Add(Environment.CurrentManagedThreadId);transport.Send(id,module,payload);
            });
            transport.Start();Check(transport.Listening && transport.LocalPort>0,"TLS composition failed to bind");
            string credential=Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            string first=await Login(transport,root,cert,credential);await Wait(()=>closed==1);
            string second=await Login(transport,root,cert,credential);await Wait(()=>closed==2);
            Check(first==second,"TLS reconnect must preserve original UUID, never create a second player");
            Check(callbackThreads.Distinct().Count()==1,"TLS lifecycle and module handlers must share one poll worker");
            Check(transport.TrySendWithOutcome("missing","test",new byte[]{1})==TransportSendOutcome.NotSent,"Missing connection cannot report submitted");
            transport.Stop();Check(!transport.Listening,"TLS stop must stop listening");
            transport.Start();Check(transport.Listening,"Stopped TLS adapter cannot restart");transport.Stop();
            framework.ShutdownExtensions();
            // Configuration mistakes must fail before any alternate transport is created.
            var invalid=new Config{Tls=new TlsServerSettings()};bool refused=false;
            try {composition.Invoke(null,new object[]{invalid});}catch(TargetInvocationException e)when(e.InnerException is System.Runtime.Serialization.SerializationException){refused=true;}
            Check(refused,"Malformed TLS configuration silently accepted/fell back");
            var missingCert=new Config{Tls=new TlsServerSettings{CertificateFile=Path.Combine(rootPath,"missing.pem"),PrivateKeyFile=privateKey}};
            using(var badContainer=(IContainer)composition.Invoke(null,new object[]{missingCert})!)
            {
                bool failed=false;try {badContainer.Resolve<IServerTransport>();}catch(Autofac.Core.DependencyResolutionException){failed=true;}
                Check(failed && !badContainer.IsRegistered<NetServer>(),"Certificate load failure started or registered UDP fallback");
            }
            // Port conflict must clean its startup worker; a later explicit start can succeed.
            using(var occupied=new TcpListener(IPAddress.Loopback,0))
            {
                occupied.Start();using var conflicted=new TlsNetServer((IPEndPoint)occupied.LocalEndpoint,cert,1,1,4096);
                bool failed=false;try {conflicted.Start();}catch(SocketException){failed=true;}
                Check(failed && !conflicted.Listening,"Bind failure silently succeeded");
                occupied.Stop();conflicted.Start();Check(conflicted.Listening,"Startup rollback retained a stale poll worker or port");conflicted.Stop();
            }
            Check(File.Exists(Path.Combine(rootPath,"users")) && File.Exists(Path.Combine(rootPath,"credentials")),"Real persistent user/credential files were not committed");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{passed=true,checks,sessionChecks,profile="TLS host / transitional existing auth packets + modern session component",realAuthenticator=true,modernBootstrapValidated=true,modernAuthenticationValidated=false,gameAssetsValidated=false,secretsInOutput=false}));
        }
        finally {Directory.Delete(rootPath,true);}
    }
}
