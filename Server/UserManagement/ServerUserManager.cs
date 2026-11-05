using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Authentication;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Utils;

namespace UserManagement
{
    /// <inheritdoc />
    /// <summary>
    /// Server-side variant of <see cref="UserManager"/>.
    /// Used to store details of each user and oversees user login.
    /// </summary>
    public class ServerUserManager : UserManager, IServerUserManager, ServerRuntime.IServerPrincipalAuthorization, ServerRuntime.IServerAuthenticatedSessionLookup
    {
        /// <inheritdoc />
        public override event EventHandler<LogEventArgs> OnLogEntry;
        /// <inheritdoc />
        public override void RaiseLogEntry(LogEventArgs e) => OnLogEntry?.Invoke(this, e);

        /// <summary>
        /// Raised when a user is logged in.
        /// </summary>
        public event EventHandler<ServerLoginEventArgs> OnLogin;

        /// <summary>
        /// <see cref="NetServer"/> to send packets and bind events to.
        /// </summary>
        private IServerTransport netServer;
        private string persistencePath;
        private readonly Func<string, string, byte[], bool> sendPacket;

        /// <summary>
        /// <see cref="ServerAuthenticator"/> to check session validity with.
        /// </summary>
        private ServerAuthenticator authenticator;
        private LegacyIdentityMappingAdapter identityAdapter;
        private string ResolvePrincipal(string connection, string session, string displayName, bool create)
        {
            if (authenticator.UsesModernIdentity)
                return authenticator.TryGetModernPrincipal(connection, session, out var principal) ? principal : null;
            return identityAdapter.ResolveSession(connection, session, displayName, create);
        }
        public LegacyIdentityMappingAdapter IdentityAdapter => identityAdapter;

        /// <summary>
        /// Collection of connected UUIDs organised by connection ID.
        /// </summary>
        private Dictionary<string, string> connectedUsers;
        /// <summary>
        /// Lock for connected user dictionary operations.
        /// </summary>
        private object connectedUsersLock = new object();
        /// <summary>
        /// Lock for user store operations.
        /// </summary>
        private readonly object userStoreLockObject = new object();

        /// <summary>
        /// Stores each user in an easily-serialisable format.
        /// </summary>
        protected override UserStore userStore { get; set; }
        /// <summary>
        /// Lock for user store operations.
        /// </summary>
        protected override object userStoreLock => userStoreLockObject;

        /// <summary>
        /// Maximum display name length for new logins.
        /// </summary>
        private int maxDisplayNameLength;

        /// <summary>
        /// Creates a new <see cref="ServerUserManager"/> instance.
        /// </summary>
        /// <param name="netServer"><see cref="NetServer"/> instance to bind packet handlers to</param>
        /// <param name="authenticator"><see cref="ServerAuthenticator"/> to check session validity with</param>
        /// <param name="maxDisplayNameLength">Maximum number of non-format characters users may have in their display name</param>
        public ServerUserManager(IServerTransport netServer, ServerAuthenticator authenticator, int maxDisplayNameLength = 100)
            : this(netServer, authenticator, maxDisplayNameLength, netServer.TrySend)
        {
        }

        // External transport seam only: business and protobuf serialization remain real.
        internal ServerUserManager(IServerTransport netServer, ServerAuthenticator authenticator, int maxDisplayNameLength,
            Func<string, string, byte[], bool> sendPacket)
        {
            this.sendPacket = sendPacket ?? throw new ArgumentNullException(nameof(sendPacket));
            this.netServer = netServer;
            this.authenticator = authenticator;
            identityAdapter = new LegacyIdentityMappingAdapter(authenticator, this);
            this.maxDisplayNameLength = maxDisplayNameLength;

            this.connectedUsers = new Dictionary<string, string>();
            this.userStore = new UserStore();

            netServer.RegisterPacketHandler(MODULE_NAME, packetHandler);
            netServer.OnConnectionClosed += connectionClosedHandler;
        }

        /// <summary>
        /// Creates a new <see cref="ServerUserManager"/> instance and loads in the user store from the given path.
        /// </summary>
        /// <param name="netServer"><see cref="NetServer"/> instance to bind packet handlers to</param>
        /// <param name="authenticator"><see cref="ServerAuthenticator"/> to check session validity with</param>
        /// <param name="userStorePath">Path to user store</param>
        /// <param name="maxDisplayNameLength">Maximum number of non-format characters users may have in their display name</param>
        public ServerUserManager(IServerTransport netServer, ServerAuthenticator authenticator, string userStorePath, int maxDisplayNameLength = 100)
        {
            this.sendPacket = netServer.TrySend;
            this.netServer = netServer;
            this.authenticator = authenticator;
            identityAdapter = new LegacyIdentityMappingAdapter(authenticator, this);
            this.maxDisplayNameLength = maxDisplayNameLength;

            this.connectedUsers = new Dictionary<string, string>();
            Load(userStorePath);

            netServer.RegisterPacketHandler(MODULE_NAME, packetHandler);
            netServer.OnConnectionClosed += connectionClosedHandler;
        }

        private void connectionClosedHandler(object sender, ConnectionEventArgs e)
        {
            string uuid;
            bool lastConnection;

            lock (connectedUsersLock)
            {
                // Make sure this connection has a UUID associated with it
                if (!connectedUsers.TryGetValue(e.ConnectionId, out uuid)) return;

                // Check if there are multiple connections for this user
                int connectionCount = 0;
                foreach (string v in connectedUsers.Values)
                {
                    if (v == uuid) connectionCount++;
                }

                lastConnection = connectionCount <= 1;
                if (!lastConnection)
                {
                    // Only drop this connection from the association dict
                    connectedUsers.Remove(e.ConnectionId);
                }
                else
                {
                    // Drop them from the connected user dictionary
                    connectedUsers.Remove(e.ConnectionId);
                }
            }

            // TryLogOut 需要获取 userStoreLock —— 在 connectedUsersLock 外调用避免死锁
            // (handleLoginPacket 路径先获取 userStoreLock 再获取 connectedUsersLock)
            if (lastConnection)
            {
                TryLogOut(uuid);
            }
        }

        /// <summary>
        /// Attempts to log in a user with the given UUID.
        /// Returns whether the user was successfully logged in.
        /// </summary>
        /// <param name="uuid">UUID of the user</param>
        /// <returns>Whether the user was successfully logged in</returns>
        /// <exception cref="ArgumentException">UUID cannot be null or empty</exception>
        public override bool TryLogIn(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) throw new ArgumentException("UUID cannot be null or empty.", nameof(uuid));
            User publicUser;
            lock (userStoreLock)
            {
                if (!userStore.Users.TryGetValue(uuid, out User user)) return false;
                user.LoggedIn = true;
                publicUser = ToPublicUser(user);
            }
            broadcastUserUpdate(publicUser);
            return true;
        }

        /// <summary>
        /// Attempts to log out a user with the given UUID.
        /// Returns whether the user was successfully logged out.
        /// </summary>
        /// <param name="uuid">UUID of the user</param>
        /// <returns>Whether the user was successfully logged out</returns>
        /// <exception cref="ArgumentException">UUID cannot be null or empty</exception>
        public override bool TryLogOut(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) throw new ArgumentException("UUID cannot be null or empty.", nameof(uuid));
            User publicUser;
            lock (userStoreLock)
            {
                if (!userStore.Users.TryGetValue(uuid, out User user)) return false;
                user.LoggedIn = false;
                publicUser = ToPublicUser(user);
            }
            broadcastUserUpdate(publicUser);
            return true;
        }

        /// <inheritdoc />
        /// <summary>
        /// Updates an existing user's properties and broadcasts the update to all connected users.
        /// Returns true if the update was successful, otherwise false.
        /// </summary>
        /// <param name="uuid">UUID of the user</param>
        /// <param name="displayName">Display name of the user</param>
        /// <param name="acceptingTrades">Whether the user is accepting trades</param>
        /// <returns>User updated successfully</returns>
        public override bool UpdateUser(string uuid, string displayName = null, bool? acceptingTrades = null)
        {
            if (!base.UpdateUser(uuid, displayName, acceptingTrades)) return false;

            User user;
            lock (userStoreLock)
            {
                if (!userStore.Users.ContainsKey(uuid)) return false;

                user = ToPublicUser(userStore.Users[uuid]);
            }

            broadcastUserUpdate(user);

            return true;
        }

        /// <summary>
        /// Saves the user store at the given path.
        /// This will overwrite the file if it already exists.
        /// </summary>
        /// <param name="filePath">Destination file path</param>
        /// <exception cref="ArgumentException">File path cannot be null or empty</exception>
        public void Save(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            lock (userStoreLock)
            {
                ServerStorage.AtomicFileSnapshot.Write(filePath, stream =>
                {
                    using var writer = new CodedOutputStream(stream, leaveOpen: true);
                    userStore.WriteTo(writer);
                });

                RaiseLogEntry(new LogEventArgs(string.Format("Saved {0} user{1}", userStore.Users.Count, userStore.Users.Count != 1 ? "s" : "")));
            }
        }

        /// <summary>
        /// Loads the user store from the given file path.
        /// Will create a new one if the file does not exist.
        /// </summary>
        /// <param name="filePath">User store file path</param>
        /// <exception cref="ArgumentException">File path cannot be null or empty</exception>
        public void Load(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            lock (userStoreLock)
            {
                // Generate a fresh user store if it doesn't exist
                if (!File.Exists(filePath))
                {
                    RaiseLogEntry(new LogEventArgs("No user database, generating a new one"));

                    // Create the user store
                    lock (userStoreLock) userStore = new UserStore();

                    // Save it
                    Save(filePath);
                }

                // Load the user store
                using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read))
                {
                    using (CodedInputStream cis = new CodedInputStream(fs))
                    {
                        userStore = UserStore.Parser.ParseFrom(cis);
                    }
                }

                persistencePath = System.IO.Path.GetFullPath(filePath);
                RaiseLogEntry(new LogEventArgs(string.Format("Loaded {0} user{1}", userStore.Users.Count, userStore.Users.Count != 1 ? "s" : "")));
            }
        }

        /// <summary>
        /// Checks whether the user with the given UUID is logged in from the given connection ID.
        /// </summary>
        /// <param name="connectionId">User's connection ID</param>
        /// <param name="uuid">User's UUID</param>
        /// <returns>User with the given UUID is logged in from the given connection ID</returns>
        // Attest the stable principal without exposing the credential mapping to session/core APIs.
        // IsLoggedIn alone can lag an authentication identity switch on the same connection.
        public bool TryGetAuthenticatedSession(string connectionId, string principalId, out string sessionId)
        {
            sessionId = null;
            if (!authenticator.TryGetAuthenticatedSessionId(connectionId, out var candidate) || !IsAuthenticatedPrincipal(connectionId, candidate, principalId)) return false;
            sessionId = candidate;
            return true;
        }

        public bool IsAuthenticatedPrincipal(string connectionId, string sessionId, string principalId)
        {
            return authenticator.IsAuthenticated(connectionId, sessionId) &&
                ResolvePrincipal(connectionId, sessionId, null, false) == principalId &&
                IsLoggedIn(connectionId, principalId);
        }

        public bool IsLoggedIn(string connectionId, string uuid)
        {
            if (string.IsNullOrEmpty(connectionId)) throw new ArgumentException("Connection ID cannot be null or empty", nameof(connectionId));
            if (string.IsNullOrEmpty(uuid)) throw new ArgumentException("UUID cannot be null or empty", nameof(uuid));

            lock (connectedUsersLock)
            {
                if (!connectedUsers.ContainsKey(connectionId)) return false;

                return connectedUsers[connectionId].Equals(uuid);
            }
        }

        /// <summary>
        /// Returns an array of connection IDs that are currently logged in.
        /// </summary>
        /// <returns></returns>
        public string[] GetConnections()
        {
            lock (connectedUsersLock) return connectedUsers.Keys.ToArray();
        }

        /// <summary>
        /// Attempts to get the connection ID associated with the given UUID.
        /// Returns whether the connection ID was gathered successfully.
        /// </summary>
        /// <param name="uuid">User's UUID</param>
        /// <param name="connectionId">Output connection ID</param>
        /// <returns>Whether the connection ID was gathered successfully</returns>
        public bool TryGetConnection(string uuid, out string connectionId)
        {
            // Initialise connection ID to something arbitrary
            connectionId = null;

            // It's a surprise tool that will help us later
            KeyValuePair<string, string> pair;

            lock (connectedUsersLock)
            {
                try
                {
                    // Try to get a single key-value pair
                    pair = connectedUsers.Single(p => p.Value == uuid);
                }
                catch (InvalidOperationException)
                {
                    // Couldn't get a single result, bail out
                    return false;
                }
            }

            // Output the connection ID and return successfully
            connectionId = pair.Key;
            return true;
        }

        /// <summary>
        /// Logs out all users.
        /// Used when shutting down.
        /// </summary>
        public void LogOutAll()
        {
            string[] uuids;
            lock (userStoreLock) uuids = userStore.Users.Keys.ToArray();
            foreach (string uuid in uuids) TryLogOut(uuid);
        }

        /// <summary>
        /// Handles incoming packets.
        /// </summary>
        /// <param name="module">Destination module</param>
        /// <param name="connectionId">Original connection ID</param>
        /// <param name="data">Data payload</param>
        private void packetHandler(string module, string connectionId, byte[] data)
        {
            // Validate the incoming packet and discard it if validation fails
            if (!ProtobufPacketHelper.ValidatePacket(typeof(ServerUserManager).Namespace, MODULE_NAME, module, data, out Any message, out TypeUrl typeUrl)) return;

            // Determine what to do with this packet type
            switch (typeUrl.Type)
            {
                case "LoginPacket":
                    RaiseLogEntry(new LogEventArgs(string.Format("Got a LoginPacket from {0}", connectionId.Highlight(HighlightType.ConnectionID))));
                    handleLoginPacket(connectionId, message.Unpack<LoginPacket>());
                    break;
                case "UserUpdatePacket":
                    RaiseLogEntry(new LogEventArgs(string.Format("Got a UserUpdatePacket from {0}", connectionId.Highlight(HighlightType.ConnectionID))));
                    handleUserUpdatePacket(connectionId, message.Unpack<UserUpdatePacket>());
                    break;
                default:
                    RaiseLogEntry(new LogEventArgs("Got an unknown packet type (" + typeUrl.Type + "), discarding...", LogLevel.DEBUG));
                    break;
            }
        }

        /// <summary>
        /// Handles incoming <see cref="LoginPacket"/>s, attempting to log in users.
        /// </summary>
        /// <param name="connectionId">Original connection ID</param>
        /// <param name="packet">Incoming packet</param>
        /// <exception cref="UserNoLongerExistsException">User no longer exists for some reason even though we just checked like a second ago</exception>
        private void handleLoginPacket(string connectionId, LoginPacket packet)
        {
            // Make sure the client is authenticated
            if (!authenticator.IsAuthenticated(connectionId, packet.SessionId))
            {
                RaiseLogEntry(new LogEventArgs("Failed login: invalid session for connection " + connectionId.Highlight(HighlightType.ConnectionID)));

                // Fail the login attempt due to an invalid session
                sendFailedLoginResponsePacket(connectionId, LoginFailureReason.SessionId, "Session is not valid, it may have expired. Try reconnecting.");

                // Stop here
                return;
            }

            string safeDisplayName = TextHelper.SanitiseRichText(packet.DisplayName ?? "");
            if (!packet.UseServerDisplayName && (string.IsNullOrWhiteSpace(packet.DisplayName) || TextHelper.StripRichText(packet.DisplayName).Length > maxDisplayNameLength))
            { sendFailedLoginResponsePacket(connectionId, LoginFailureReason.DisplayName, "Display name is empty or too long."); return; }
            string uuid;
            try { uuid = ResolvePrincipal(connectionId, packet.SessionId, safeDisplayName, true); }
            catch (ArgumentException) { sendFailedLoginResponsePacket(connectionId, LoginFailureReason.DisplayName, "Display name is invalid."); return; }
            if (uuid == null) { sendFailedLoginResponsePacket(connectionId, LoginFailureReason.SessionId, "Authenticated identity is unavailable."); return; }
            lock (userStoreLock)
            {
                if (!userStore.Users.ContainsKey(uuid))
                {
                    if (string.IsNullOrWhiteSpace(packet.DisplayName)) { sendFailedLoginResponsePacket(connectionId, LoginFailureReason.DisplayName, "Display name is required."); return; }
                    userStore.Users.Add(uuid, new User { Uuid = uuid, DisplayName = safeDisplayName, LoggedIn = false });
                }
            }
            if (!TryLogIn(uuid)) { sendFailedLoginResponsePacket(connectionId, LoginFailureReason.InternalServerError, "Player record unavailable."); return; }

            // Check if they want to use the display name stored on the server
            string displayName = packet.DisplayName;
            if (packet.UseServerDisplayName)
            {
                // Try to get the display name stored server-side
                if (!TryGetDisplayName(uuid, out displayName))
                {
                    // This should never happen because we just made sure that the user existed no more than 30 lines ago, but just in case...
                    RaiseLogEntry(new LogEventArgs(string.Format("Failed to log in user {0} after checking if they want to use their display name from the server. This shouldn't happen.", uuid.Highlight(HighlightType.UUID)), LogLevel.ERROR));

                    // Fail the login attempt due to a weird error
                    sendFailedLoginResponsePacket(connectionId, LoginFailureReason.InternalServerError, "Failed to log in after checking server display name preference. This should not happen, please report it to the developer.");

                    // Stop here
                    return;
                }
            }
            else
            {
                // Check the length of their username without markup
                if (TextHelper.StripRichText(packet.DisplayName).Length > maxDisplayNameLength)
                {
                    // Fail the login attempt due to display name length
                    sendFailedLoginResponsePacket(connectionId, LoginFailureReason.DisplayName, "Display name is too long.");

                    // Stop here
                    return;
                }

                // Update the user's display name on the server with the one they've provided

            }

            // Login preferences are current authenticated user intent, for every protocol.
            UpdateUser(uuid, TextHelper.SanitiseRichText(displayName), packet.AcceptingTrades);

            // Publish a login only after its credential and stable UUID mapping are persisted.
            // Unhosted component callers without Load retain their existing in-memory contract.
            if (persistencePath != null)
            {
                try { authenticator.FlushLoadedCredentials(); Save(persistencePath); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    lock (connectedUsersLock) connectedUsers.Remove(connectionId);
                    RaiseLogEntry(new LogEventArgs("Login refused: identity snapshot could not be committed.", LogLevel.ERROR));
                    sendFailedLoginResponsePacket(connectionId, LoginFailureReason.InternalServerError, "Identity persistence unavailable. Reconnect after server recovery.");
                    return;
                }
            }

            // Add their UUID/Session ID pair to connectedUsers
            lock (connectedUsersLock)
            {
                // Remove an existing session, if it exists
                connectedUsers.Remove(connectionId);

                // Add this session with the freshly-logged in UUID
                connectedUsers.Add(connectionId, uuid);
            }

            // Log the event
            RaiseLogEntry(new LogEventArgs("User logged in (Connection: " + connectionId.Highlight(HighlightType.ConnectionID) + ", UUID: " + uuid.Highlight(HighlightType.UUID) + ")"));

            // Send a successful login response
            sendSuccessfulLoginResponsePacket(connectionId, uuid, displayName);

            // Send a sync packet with the current user list
            sendSyncPacket(connectionId);

            // Raise the OnLogin event
            OnLogin?.Invoke(this, new ServerLoginEventArgs(connectionId, uuid));
        }

        /// <summary>
        /// Sends a successful <see cref="LoginResponsePacket"/> with the given UUID and display name.
        /// </summary>
        /// <param name="connectionId">Connection ID</param>
        /// <param name="uuid">User's UUID</param>
        /// <param name="displayName">User's display name</param>
        private void sendSuccessfulLoginResponsePacket(string connectionId, string uuid, string displayName)
        {
            RaiseLogEntry(new LogEventArgs(string.Format("Sending successful LoginResponsePacket to connection {0}", connectionId.Highlight(HighlightType.ConnectionID)), LogLevel.DEBUG));

            // Create and pack a response
            LoginResponsePacket response = new LoginResponsePacket
            {
                Success = true,
                Uuid = uuid,
                DisplayName = displayName
            };
            Any packedResponse = ProtobufPacketHelper.Pack(response);

            // Send it on its way
            if (!sendPacket(connectionId, MODULE_NAME, packedResponse.ToByteArray()))
            {
                RaiseLogEntry(new LogEventArgs("Failed to send LoginResponsePacket to connection " + connectionId.Highlight(HighlightType.ConnectionID) + ", disconnecting", LogLevel.ERROR));
                netServer.DisconnectPeer(connectionId);
            }
        }

        /// <summary>
        /// Sends a failed <see cref="LoginResponsePacket"/> with the given reason and message.
        /// </summary>
        /// <param name="connectionId">Connection ID</param>
        /// <param name="failureReason">Failure reason</param>
        /// <param name="failureMessage">Failure message</param>
        private void sendFailedLoginResponsePacket(string connectionId, LoginFailureReason failureReason, string failureMessage)
        {
            RaiseLogEntry(new LogEventArgs(string.Format("Sending failed LoginResponsePacket to connection {0}", connectionId.Highlight(HighlightType.ConnectionID)), LogLevel.DEBUG));

            // Create and pack a response
            LoginResponsePacket response = new LoginResponsePacket
            {
                Success = false,
                FailureReason = failureReason,
                FailureMessage = failureMessage
            };
            Any packedResponse = ProtobufPacketHelper.Pack(response);

            // Send it on its way
            if (!sendPacket(connectionId, MODULE_NAME, packedResponse.ToByteArray()))
            {
                RaiseLogEntry(new LogEventArgs("Failed to send LoginResponsePacket to connection " + connectionId.Highlight(HighlightType.ConnectionID) + ", disconnecting", LogLevel.ERROR));
                netServer.DisconnectPeer(connectionId);
            }
        }

        /// <summary>
        /// Packs and sends the given user to all currently-logged in users.
        /// Used when a user's state changes.
        /// </summary>
        /// <param name="user">User to broadcast</param>
        // Explicit allowlist drops credentials AND protobuf unknown fields. The returned
        // object is a detached wire snapshot, never a reference into the identity store.
        private static User ToPublicUser(User user) => new User
        {
            Uuid = user.Uuid,
            DisplayName = user.DisplayName,
            LoggedIn = user.LoggedIn,
            AcceptingTrades = user.AcceptingTrades
        };

        private void broadcastUserUpdate(User publicUser)
        {
            UserUpdatePacket packet = new UserUpdatePacket { User = ToPublicUser(publicUser) };
            byte[] bytes = ProtobufPacketHelper.Pack(packet).ToByteArray();
            string[] recipients = GetConnections();
            foreach (string connectionId in recipients)
            {
                if (!sendPacket(connectionId, MODULE_NAME, bytes))
                    RaiseLogEntry(new LogEventArgs("Failed to send UserUpdatePacket to connection " + connectionId.Highlight(HighlightType.ConnectionID), LogLevel.ERROR));
            }
        }

        /// <summary>
        /// Sends a <see cref="UserSyncPacket"/> containing all users to the given connection.
        /// Usernames are stripped from users for security.
        /// </summary>
        /// <param name="connectionId">Destination connection ID</param>
        private void sendSyncPacket(string connectionId)
        {
            // Create a blank sync packet
            UserSyncPacket packet = new UserSyncPacket();

            lock (userStoreLock)
            {
                // Add users to the sync packet
                foreach (User userRef in userStore.Users.Values)
                {
                    packet.Users.Add(ToPublicUser(userRef));
                }
            }

            // Pack it
            Any packedPacket = ProtobufPacketHelper.Pack(packet);

            // Send it on its way
            if (!sendPacket(connectionId, MODULE_NAME, packedPacket.ToByteArray()))
            {
                RaiseLogEntry(new LogEventArgs("Failed to send UserSyncPacket to connection " + connectionId.Highlight(HighlightType.ConnectionID), LogLevel.ERROR));
            }
        }

        /// <summary>
        /// Handles incoming <see cref="UserUpdatePacket"/>s.
        /// </summary>
        /// <param name="connectionId">Original connection ID</param>
        /// <param name="packet">Incoming packet</param>
        private void handleUserUpdatePacket(string connectionId, UserUpdatePacket packet)
        {
            // Discard packets from non-authenticated sessions
            if (!authenticator.IsAuthenticated(connectionId, packet.SessionId)) return;

            // Discard packets from non-logged in sessions
            if (!IsLoggedIn(connectionId, packet.Uuid)) return;

            User user = packet.User;

            lock (connectedUsersLock)
            {
                // Discard packets trying to update users other than themselves
                if (user.Uuid != connectedUsers[connectionId]) return;
            }

            // Discard packets with display names longer than the limit
            if (TextHelper.StripRichText(user.DisplayName).Length > maxDisplayNameLength) return;

            // Update the user
            UpdateUser(user.Uuid, TextHelper.SanitiseRichText(user.DisplayName), user.AcceptingTrades);
        }
    }
}
