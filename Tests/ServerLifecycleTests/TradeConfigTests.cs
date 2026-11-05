using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Xml.Linq;
using PhinixServer;

internal static class TradeConfigTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        Type type = Assembly.LoadFrom(Path.Combine(Environment.CurrentDirectory, "Server/bin/Release/net10.0/Extensions/TradeExtension.Server.dll"))
            .GetType("Phinix.TradeExtension.Server.TradeServerConfig", true)!;
        var get = typeof(Config).GetMethod("GetConfig")!.MakeGenericMethod(type);
        var save = typeof(Config).GetMethod("SaveConfig")!.MakeGenericMethod(type);
        object Read(Config config) => get.Invoke(config, null)!;
        int Number(object value, string name) => (int)type.GetProperty(name)!.GetValue(value)!;
        var host = new Config(); object section = Read(host);
        Check(Number(section, "PrincipalRetainedTradeLimit") == 32 && Number(section, "RecoveryPageSize") == 8, "Trade defaults not loaded");
        foreach (string field in new[] { "PrincipalRetainedTradeLimit", "PrincipalPayloadByteLimit", "PrincipalAuxiliaryByteLimit" }) type.GetProperty(field)!.SetValue(section, 0);
        type.GetProperty("RecoveryPageSize")!.SetValue(section, 1); save.Invoke(host, new[] { section });
        object zero = Read(host); Check(Number(zero, "PrincipalRetainedTradeLimit") == 0 && Number(zero, "PrincipalPayloadByteLimit") == 0 && Number(zero, "RecoveryPageSize") == 1, "Explicit disabled quota lost in provider roundtrip");
        var xml = XDocument.Parse(host.ExtensionConfigs["builtin.trade"]);
        foreach (var e in xml.Root!.Elements().Where(e => e.Name.LocalName != "DatabasePath").ToArray()) e.Remove();
        host.ExtensionConfigs["builtin.trade"] = xml.ToString();
        object old = Read(host); Check(Number(old, "PrincipalRetainedTradeLimit") == 32 && Number(old, "PrincipalPayloadByteLimit") == 1048576 && Number(old, "RecoveryPageSize") == 8, "Old sections accidentally disabled missing limits");
        foreach (string bad in new[] { "<invalid-synthetic-secret", host.ExtensionConfigs["builtin.trade"].Replace("TradeServerConfig", "WrongSection") })
        {
            host.ExtensionConfigs["builtin.trade"] = bad;
            try { Read(host); throw new Exception("Malformed section silently reset to defaults"); }
            catch (TargetInvocationException error) when (error.InnerException is SerializationException) { Check(!error.InnerException.Message.Contains("synthetic-secret"), "Config failure exposed arbitrary input"); }
        }
        string directory = Path.Combine(Path.GetTempPath(), "phinix-config-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "server.conf"), candidate = source + ".next", second = source + ".second";
            var original = new Config { Port = 20099 }; original.ExtensionConfigs["unrelated.plugin"] = "<private>synthetic-sentinel</private>"; original.Save(source);
            byte[] before = File.ReadAllBytes(source);
            int Tool(string input, string output, params string[] options)
            {
                var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string arg in new[] { "tools/configure_trade_limits.py", "--input", input, "--output", output }.Concat(options)) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                string stdout = process.StandardOutput.ReadToEnd(), stderr = process.StandardError.ReadToEnd();
                Check(process.WaitForExit(10000), "Config tool deadline");
                Check(!stdout.Contains("synthetic-sentinel") && !stderr.Contains("synthetic-sentinel"), "Config tool exposed unrelated values");
                return process.ExitCode;
            }
            Check(Tool(source, candidate, "--retained-trades", "0", "--payload-bytes", "0", "--auxiliary-bytes", "0", "--page-size", "2") == 0, "Configuration candidate tool failed");
            var parsed = Config.Load(candidate); object edited = Read(parsed);
            Check(Number(edited, "PrincipalRetainedTradeLimit") == 0 && Number(edited, "RecoveryPageSize") == 2 && parsed.Port == 20099 && parsed.ExtensionConfigs["unrelated.plugin"] == original.ExtensionConfigs["unrelated.plugin"], "Real config roundtrip lost settings");
            Check(Tool(candidate, second, "--payload-bytes", "4096") == 0 && Number(Read(Config.Load(second)), "PrincipalPayloadByteLimit") == 4096, "Existing nested trade section update failed");
            Check(Tool(source, source, "--retained-trades", "1") != 0 && before.SequenceEqual(File.ReadAllBytes(source)), "Tool overwrote source");
            Check(Tool(source, candidate, "--retained-trades", "1") != 0, "Tool overwrote existing output");
            Check(Tool(source, source + ".bad", "--page-size", "0") != 0 && !File.Exists(source + ".bad"), "Invalid config candidate published");
            Type adapterType = Assembly.LoadFrom(Path.Combine(Environment.CurrentDirectory, "Server/bin/Release/net10.0/Extensions/LegacyAdapter.Server.dll"))
                .GetType("Phinix.LegacyAdapter.Server.OriginalTradeServerConfig", true)!;
            var readAdapter = typeof(Config).GetMethod("GetConfig")!.MakeGenericMethod(adapterType);
            bool Enabled(string path) => (bool)adapterType.GetProperty("Enabled")!.GetValue(readAdapter.Invoke(Config.Load(path), null))!;
            Check(Enabled(source), "Original trading was not enabled with no saved section");
            string enabled = source + ".original-on", disabled = source + ".original-off";
            Check(Tool(source, enabled, "--original-trading", "on") == 0 && Enabled(enabled), "Original adapter candidate not readable by actual provider");
            Check(Tool(enabled, disabled, "--original-trading", "off") == 0 && !Enabled(disabled), "Original adapter could not be disabled");
            Type chatAdapterType = adapterType.Assembly.GetType("Phinix.LegacyAdapter.Server.LegacyChatServerConfig", true)!;
            var readChatAdapter = typeof(Config).GetMethod("GetConfig")!.MakeGenericMethod(chatAdapterType);
            string both = source + ".both-adapters", chatOff = source + ".chat-off";
            bool ChatEnabled(string path) => (bool)chatAdapterType.GetProperty("Enabled")!.GetValue(readChatAdapter.Invoke(Config.Load(path), null))!;
            Check(ChatEnabled(source), "Original chat was not enabled with no saved section");
            Check(Tool(enabled, both, "--original-chat", "on") == 0 && Enabled(both) && ChatEnabled(both), "Chat enable lost trade config or could not be read by actual provider");
            Check(Tool(both, chatOff, "--original-chat", "off") == 0 && Enabled(chatOff) && !ChatEnabled(chatOff), "Chat disable changed independent trade switch");
            Check(before.SequenceEqual(File.ReadAllBytes(source)) && Config.Load(disabled).ExtensionConfigs["unrelated.plugin"] == original.ExtensionConfigs["unrelated.plugin"], "Original adapter edit changed source or unrelated settings");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(source) == File.GetUnixFileMode(candidate), "Config candidate permissions changed");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("TRADE CONFIG RESULT passed; defaults/legacy-missing/explicit-zero/real-provider/corrupt-refusal/python-candidate-roundtrip");
    }
}
