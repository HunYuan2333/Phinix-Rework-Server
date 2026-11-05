using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Timers;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Autofac;
using ServerRuntime;
using Timer = System.Timers.Timer;
using Authentication;
using Connections;
using PhinixServer.Framework;
using UserManagement;
using Utils;
using Utils.Framework;

namespace PhinixServer
{
    class Server
    {
        // Default config file constant
        private const string CONFIG_FILE = "server.conf";

        public static readonly Version Version = Assembly.GetAssembly(typeof(Server)).GetName().Version;

        // Module instance variables
        public static Config Config;
        public static Logger Logger;
        public static IServerTransport Connections;
        public static ServerAuthenticator Authenticator;
        public static ServerUserManager UserManager;
        public static PhinixFrameworkServer Framework;

        private static readonly CancellationTokenSource shutdownRequested = new CancellationTokenSource();
        private static ServerSaveLifecycle saveLifecycle;

        // Save timer
        private static Timer saveTimer;

        static void Main()
        {
            try { Run(); }
            catch (Exception error)
            {
                // Startup failures must not invoke the normal final-save path or expose payloads.
                Environment.ExitCode = 1;
                Console.Error.WriteLine("Server startup failed: " + error.GetType().Name);
            }
        }

        private static void Run()
        {
            // Read in the config and initialise the logging module
            const string identityModeAnchor = "identity-authority.required";
            if (System.IO.File.Exists(identityModeAnchor) && !System.IO.File.Exists(CONFIG_FILE))
                throw new System.IO.IOException("Expected modern server configuration is missing; refusing default identity settings.");
            Config = Config.Load(CONFIG_FILE);
            if (System.IO.File.Exists(identityModeAnchor) && (Config.ModernIdentity == null || !System.IO.File.Exists(Config.ModernIdentity.DatabaseFile)))
                throw new System.IO.IOException("Expected modern authority is missing or disabled; refusing an identity downgrade.");
            Logger = new Logger(Config.LogPath, Config.DisplayVerbosity, Config.LogVerbosity);

            // Set up module instances
            using IContainer container = ServerComposition.Build(Config);
            using ILifetimeScope application = container.BeginLifetimeScope();
            Connections = application.Resolve<IServerTransport>();
            Authenticator = application.Resolve<ServerAuthenticator>();
            UserManager = application.Resolve<ServerUserManager>();
            var workQueue = application.Resolve<ServerWorkQueue>();
            var dispatcher = new FrameworkServerPacketDispatcher();
            EventHandler drainWork = (_, _) => workQueue.Drain();
            var faultSource = Connections as IServerTransportFaultSource;
            Action<string> onTransportFailure = errorType =>
            {
                Environment.ExitCode = 1;
                RequestShutdown();
                Console.Error.WriteLine("Server transport worker failed: " + errorType);
            };
            saveLifecycle = new ServerSaveLifecycle(SaveStates);
            using var timer = new Timer(Config.SaveInterval) { AutoReset = true };
            saveTimer = timer;

            try
            {
                Authenticator.OnLogEntry += ILoggableHandler;
                UserManager.OnLogEntry += ILoggableHandler;
                Connections.PollCompleted += drainWork;
                if (faultSource != null) faultSource.WorkerFailed += onTransportFailure;
                timer.Elapsed += saveHandler;
                RunApplication(application, identityModeAnchor, workQueue, dispatcher);
            }
            catch
            {
                // Unwind while the application scope and the services borrowed by plugins still exist.
                try { saveLifecycle.StopWithoutSave(Quiesce); }
                catch (Exception error) { Console.Error.WriteLine("Server cleanup failed: " + error.GetType().Name); }
                throw;
            }
            finally
            {
                timer.Elapsed -= saveHandler;
                dispatcher.ConfigureWithResult(null);
                Connections.PollCompleted -= drainWork;
                if (faultSource != null) faultSource.WorkerFailed -= onTransportFailure;
                Authenticator.OnLogEntry -= ILoggableHandler;
                UserManager.OnLogEntry -= ILoggableHandler;
                if (Framework != null) Framework.OnLogEntry -= ILoggableHandler;
            }
        }

        private static void RunApplication(ILifetimeScope application, string identityModeAnchor,
            ServerWorkQueue workQueue, FrameworkServerPacketDispatcher frameworkPacketDispatcher)
        {
            // Refuse corrupt core snapshots before activating any extensions.
            Authenticator.Load(Config.CredentialDatabasePath);
            UserManager.Load(Config.UserDatabasePath);
            if (Config.ModernIdentity != null)
            {
                application.Resolve<PasswordlessIdentityEndpoint>();
                IdentityAuthorityBootstrap.PinMode(identityModeAnchor, application.Resolve<PasswordlessIdentityStore>().ServerId);
            }
            ExtensionHostContext extensionHostContext = new ExtensionHostContext
            {
                HostKind = "server",
                Log = (message, level) => ILoggableHandler(typeof(Server), new LogEventArgs(message, level)),
                StorageProvider = new FileSystemExtensionStorageProvider(System.IO.Path.Combine("framework-extensions", "server"))
            };
            extensionHostContext.AddService<ServerStorage.IOperationJournalFactory>(application.Resolve<ServerStorage.IOperationJournalFactory>());
            extensionHostContext.AddService(UserManager);
            extensionHostContext.AddService<IServerUserManager>(UserManager);
            extensionHostContext.AddService<IServerTransport>(Connections);
            extensionHostContext.AddService<IServerPrincipalAuthorization>(UserManager);
            extensionHostContext.AddService<IServerWorkQueue>(workQueue);
            extensionHostContext.AddService<IFrameworkServerPacketDispatcher>(frameworkPacketDispatcher);
            extensionHostContext.AddService<IResultServerPacketDispatcher>(frameworkPacketDispatcher);
            extensionHostContext.AddService<IExtensionConfigProvider>(Config);
            ExtensionAssemblyLoader.LoadAssemblies(
                GetExtensionProbeDirectories(),
                (message, level) => ILoggableHandler(typeof(Server), new LogEventArgs(message, level)));
            Framework = new PhinixFrameworkServer(Connections, Authenticator, UserManager, extensionHostContext);
            frameworkPacketDispatcher.ConfigureWithResult((connectionId, sourceExtensionId, packet) =>
                Framework?.DispatchExtensionPacketWithResult(sourceExtensionId, connectionId, packet) ??
                PacketDispatchResult.Single(connectionId, global::Connections.TransportSendOutcome.NotSent));

            // Add handler for ILoggable modules
            Framework.OnLogEntry += ILoggableHandler;

            Console.CancelKeyPress += RequestShutdown;
            try
            {
                using var terminateSignal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    RequestShutdown();
                });
                if (!shutdownRequested.IsCancellationRequested)
                {
                    Connections.Start();
                    Logger.Log(Verbosity.INFO, Config.ModernIdentity != null ? "Accepting TLS passwordless identity v1" : string.Format("Accepting auth type \"{0}\"", Config.AuthType.ToString()));
                    Logger.Log(Verbosity.INFO, string.Format("Phinix server version {0} listening on {1}:{2}", Version, Connections.Endpoint.Address, Connections.LocalPort));
                    saveTimer.Start();
                    RunConsole();
                    StopAndSave();
                }
                else
                {
                    saveLifecycle.StopWithoutSave(Quiesce);
                }
            }
            finally { Console.CancelKeyPress -= RequestShutdown; }
        }

        private static void RunConsole()
        {
            // Start interpreting commands
            CommandInterpreter interpreter = new CommandInterpreter(
                () => Framework?.ResolveExtensionApis<IServerConsoleCommandProvider>() ?? Array.Empty<IServerConsoleCommandProvider>());
            while (!shutdownRequested.IsCancellationRequested)
            {
                // Wait until we've got a command to interpret
                string line;
                try
                {
                    line = Task.Run(() => Console.ReadLine()).WaitAsync(shutdownRequested.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { break; }
                if (line == null)
                {
                    // Dedicated/background server: EOF disables console input, not the server.
                    shutdownRequested.Token.WaitHandle.WaitOne();
                    break;
                }

                // Don't do anything if the command is empty
                if (string.IsNullOrEmpty(line)) continue;

                // Break the line down into the command and its arguments
                List<string> arguments = new List<string>(line.Split(' '));
                string command = arguments.First();
                arguments.RemoveAt(0); // Remove the command from the argument list

                // Check if we've been given the exit command
                // This is checked here to avoid weird workarounds from the interpreter
                if (command == "exit" || command == "quit" || command == "stop")
                {
                    RequestShutdown();
                    break;
                }

                // Interpret the command and its arguments
                interpreter.Run(command, arguments);
            }
        }

        private static IEnumerable<string> GetExtensionProbeDirectories()
        {
            string appBaseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(appBaseDirectory))
            {
                yield break;
            }

            yield return Path.Combine(appBaseDirectory, "Extensions");
            yield return Path.Combine(appBaseDirectory, "UserExtensions");
        }

        /// <summary>
        /// Handles save events.
        /// Used by <see cref="saveTimer"/> to save each module state periodically.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void saveHandler(object sender, ElapsedEventArgs e)
        {
            try { saveLifecycle.TrySave(); }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                // Do not log arbitrary exception messages from persistence implementations.
                Console.Error.WriteLine("Server save failed: " + error.GetType().Name);
            }
        }

        private static void SaveStates()
        {
            Authenticator.Save(Config.CredentialDatabasePath);
            UserManager.Save(Config.UserDatabasePath);
            if (!Framework.TrySaveExtensionState())
                throw new IOException("One or more extension states could not be saved.");
            Config.Save(CONFIG_FILE);
        }

        private static void RequestShutdown(object sender = null, EventArgs e = null)
        {
            if (e is ConsoleCancelEventArgs consoleEvent) consoleEvent.Cancel = true;
            shutdownRequested.Cancel();
        }

        private static void Quiesce()
        {
            saveTimer.Stop();
            Connections.Stop();
            Framework?.ShutdownExtensions();
            UserManager.LogOutAll();
        }

        private static void StopAndSave()
        {
            try { saveLifecycle.Stop(Quiesce); }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine("Server shutdown failed: " + error.GetType().Name);
            }
        }

        /// <summary>
        /// Handler for <see cref="ILoggable"/> <c>OnLogEvent</c> events.
        /// Raised by modules as a way to hook into the server log.
        /// </summary>
        /// <param name="sender">Object that raised the event</param>
        /// <param name="args">Event arguments</param>
        private static void ILoggableHandler(object sender, LogEventArgs args)
        {
            Verbosity verbosity;
            switch (args.LogLevel)
            {
                case LogLevel.DEBUG:
                    verbosity = Verbosity.DEBUG;
                    break;
                case LogLevel.WARNING:
                    verbosity = Verbosity.WARN;
                    break;
                case LogLevel.ERROR:
                    verbosity = Verbosity.ERROR;
                    break;
                case LogLevel.FATAL:
                    verbosity = Verbosity.FATAL;
                    break;
                case LogLevel.INFO:
                default:
                    verbosity = Verbosity.INFO;
                    break;
            }

            Type sourceType = sender as Type;
            string module = sourceType != null ? (sourceType.Namespace ?? sourceType.Name) : sender?.GetType().Namespace;
            Logger.Log(verbosity, args.Message, module);
        }
    }
}
