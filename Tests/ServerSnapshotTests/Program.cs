using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PhinixServer;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using ServerStorage;
using Utils.Framework;

internal static class Program
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static readonly Type Store = typeof(PhinixFrameworkTradeServerService).Assembly.GetType("Phinix.TradeExtension.Server.PhinixFrameworkTradeStore");
    private static object NewStore() => Activator.CreateInstance(Store, nonPublic: true);
    private static object Call(object store, string method, params object[] args) => Store.GetMethod(method).Invoke(store, args);

    private static string MakeTrade(string path)
    {
        var store = NewStore();
        object[] create = { "synthetic-a", "synthetic-b", null, null };
        Require((bool)Call(store, "TryCreate", create), "real trade create");
        string id = ((FrameworkTradeStateSnapshot)create[2]).TradeId;
        var item = new FrameworkItemPayload { CodecId = "synthetic.opaque-v1", PayloadBytes = new byte[] { 0, 17, 255 }, PayloadJson = "{\"synthetic\":true}" };
        object[] offer = { id, "synthetic-a", new[] { item }, null, null };
        Require((bool)Call(store, "TrySetOffer", offer), "real offer update");
        Call(store, "Save", path);
        return id;
    }

    private static void AssertTrade(string path, string id)
    {
        var loaded = NewStore();
        Call(loaded, "Load", path);
        var trades = (FrameworkTradeStateSnapshot[])Call(loaded, "GetByParticipant", "synthetic-a");
        Require(trades.Length == 1 && trades[0].TradeId == id, "same real persisted trade");
        var item = trades[0].Participants.Single(p => p.Uuid == "synthetic-a").ItemsOnOffer.Single();
        Require(item.CodecId == "synthetic.opaque-v1" && item.PayloadBytes.SequenceEqual(new byte[] { 0, 17, 255 }), "opaque offer preserved");
    }

    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--blocked-write")
        {
            AtomicFileSnapshot.Write(args[1], stream =>
            {
                stream.Write(new byte[] { 60, 98, 114, 111, 107, 101, 110 });
                stream.Flush();
                File.WriteAllText(args[2], "snapshot.temp-written-before-publish");
                Thread.Sleep(Timeout.Infinite);
            });
            return;
        }
        string directory = Path.Combine(Path.GetTempPath(), "phinix-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string config = Path.Combine(directory, "server.conf");
            new Config { Port = 20001 }.Save(config);
            byte[] previous = File.ReadAllBytes(config);
            try
            {
                AtomicFileSnapshot.Write(config, stream => { stream.WriteByte(60); throw new IOException("synthetic serialization failure"); });
                throw new Exception("serializer failure ignored");
            }
            catch (IOException) { }
            Require(previous.SequenceEqual(File.ReadAllBytes(config)) && Config.Load(config).Port == 20001, "failed serialization keeps prior parseable config");
            Require(Directory.GetFiles(directory, ".*.tmp").Length == 0, "failed write cleaned private temp");
            new Config { Port = 20002 }.Save(config);
            Require(Config.Load(config).Port == 20002, "real config replacement readable");
            if (!OperatingSystem.IsWindows())
            {
                Require(File.GetUnixFileMode(config) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "new snapshot private permissions");
                File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                new Config { Port = 20003 }.Save(config);
                Require(File.GetUnixFileMode(config) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead), "existing Unix mode bits retained");
            }
            string blocked = Path.Combine(directory, "blocked");
            Directory.CreateDirectory(blocked);
            try { AtomicFileSnapshot.Write(blocked, s => s.WriteByte(1)); throw new Exception("directory destination accepted"); }
            catch (IOException) { }
            Require(Directory.Exists(blocked) && Directory.GetFiles(directory, ".*.tmp").Length == 0, "invalid destination untouched");
            string link = Path.Combine(directory, "snapshot-link");
            File.CreateSymbolicLink(link, config);
            try { AtomicFileSnapshot.Write(link, s => s.WriteByte(1)); throw new Exception("symlink destination accepted"); }
            catch (IOException) { }
            Require(Config.Load(config).Port == (OperatingSystem.IsWindows() ? 20002 : 20003), "symlink target unchanged");

            string trade = Path.Combine(directory, "trade-state.bin");
            string id = MakeTrade(trade);
            AssertTrade(trade, id);
            previous = File.ReadAllBytes(trade);
            string barrier = Path.Combine(directory, "barrier");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--blocked-write", trade, barrier }) start.ArgumentList.Add(argument);
            using (var child = Process.Start(start))
            {
                try
                {
                    var deadline = Stopwatch.StartNew();
                    while (!File.Exists(barrier) && !child.HasExited && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                    Require(File.Exists(barrier) && !child.HasExited, "child hit pre-publish barrier");
                    var orphan = Directory.GetFiles(directory, ".trade-state.bin.*.tmp").Single();
                    if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(orphan) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "incomplete temporary snapshot is private");
                    child.Kill(entireProcessTree: true);
                    Require(child.WaitForExit(5000), "killed writer exited");
                }
                finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); } }
            }
            Require(previous.SequenceEqual(File.ReadAllBytes(trade)), "process kill preserved exact previous trade bytes");
            AssertTrade(trade, id);
            Require(Directory.GetFiles(directory, ".trade-state.bin.*.tmp").Length == 1, "orphan remains unpromoted after kill");
            Console.WriteLine("SNAPSHOT RESULT passed; scenarios=6; killedBeforePublish=true; realTradeReload=true");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
