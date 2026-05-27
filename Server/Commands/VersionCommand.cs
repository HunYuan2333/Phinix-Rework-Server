using System;
using System.Collections.Generic;
using System.Reflection;
using Authentication;
using Connections;
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
            Console.WriteLine("FrameworkChat: " + GetAssemblyVersion("ChatExtension.Server"));
            Console.WriteLine("FrameworkTrade: " + GetAssemblyVersion("TradeExtension.Server"));

            return true;
        }

        private static string GetAssemblyVersion(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return "unknown";
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly.GetName().Version?.ToString() ?? "unknown";
                }
            }

            return "not loaded";
        }
    }
}
