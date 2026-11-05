using System;
using System.IO;
using PhinixServer;

internal static class StartupValidationTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phinix-config-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            int index = 0;
            foreach (var config in new[] { new Config { Port = -1 }, new Config { Port = 65536 },
                new Config { MaxConnections = 0 }, new Config { SaveInterval = 0 }, new Config { MaxDisplayNameLength = 0 } })
            {
                string path = Path.Combine(directory, (index++) + ".conf");
                config.Save(path);
                bool refused = false;
                try { Config.Load(path); } catch { refused = true; }
                if (!refused) throw new Exception("Invalid deserialized core config accepted: case " + index);
            }
            string valid = Path.Combine(directory, "ephemeral.conf");
            new Config { Port = 0 }.Save(valid);
            if (Config.Load(valid).Port != 0) throw new Exception("Explicit ephemeral test port not preserved");
            var composition = typeof(Config).Assembly.GetType("PhinixServer.ServerComposition");
            bool restricted = false;
            try { composition.GetMethod("Build", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, new object[] { new Config { Address = System.Net.IPAddress.Loopback } }); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is NotSupportedException) { restricted = true; }
            if (!restricted) throw new Exception("Restrictive bind silently widened");
            Console.WriteLine("STARTUP CONFIG RESULT passed; invalid=5; ephemeral=1; restricted-bind-refused=1");
        }
        finally { Directory.Delete(directory, true); }
    }
}
