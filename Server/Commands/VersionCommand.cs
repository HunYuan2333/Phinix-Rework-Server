using System;
using System.Collections.Generic;
using Authentication;
using Connections;
using PhinixServer.Framework;
using UserManagement;

namespace PhinixServer
{
    /// <inheritdoc />
    public class VersionCommand : Command
    {
        public override string CommandName => "version";

        public override HelpEntry[] HelpEntries => new HelpEntry[]
        {
            new HelpEntry("version", "Displays the version of the server and its modules")
        };

        public override bool Execute(List<string> args)
        {
            Console.WriteLine("Server: " + Server.Version);
            Console.WriteLine("Connections: " + NetCommon.Version);
            Console.WriteLine("Authentication: " + Authenticator.Version);
            Console.WriteLine("UserManagement: " + UserManager.Version);
            Console.WriteLine("FrameworkChat: " + Server.Version);
            Console.WriteLine("FrameworkTrade: " + typeof(PhinixFrameworkTradeServerService).Assembly.GetName().Version);

            return true;
        }
    }
}
