using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ServerStorage;

internal static class Program
{
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static object Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }

    private static void UsageMigrationAndBudget(string path)
    {
        var journal=SqliteOperationJournal.CreateNew(path);
        journal.Execute("owner","initial",B("initial"),tx=>{tx.WriteState("item",B("old"));return B("saved");});
        using(var db=new SqliteConnection("Data Source="+path))
        {db.Open();using var q=db.CreateCommand();q.CommandText="SELECT name FROM sqlite_schema WHERE type='trigger' AND name LIKE 'usage_%'";
            var names=new System.Collections.Generic.List<string>();using(var rows=q.ExecuteReader())while(rows.Read())names.Add(rows.GetString(0));
            foreach(string name in names){q.CommandText="DROP TRIGGER "+name;q.ExecuteNonQuery();}q.CommandText="DROP TABLE journal_usage; PRAGMA user_version=1";q.ExecuteNonQuery();}
        journal=SqliteOperationJournal.OpenExisting(path);Check((long)Scalar(path,"PRAGMA user_version")==2,"Version 1 not migrated");
        Check(journal.Execute("owner","initial",B("initial"),_=>throw new Exception("Replay applied business"),1).Replayed,"Storage cap blocked original committed result");
        Throws<JournalCapacityException>(()=>journal.Execute("owner","physical-cap",B("cap"),_=>B("bad"),1));
        long usage=(long)Scalar(path,"SELECT bytes FROM journal_usage");
        Throws<JournalCapacityException>(()=>journal.Execute("owner","rejected",B("rejected"),tx=>{tx.WriteState("item",B("changed"));tx.EnsureStorageBudget(1,100);return B("bad");}));
        Check((long)Scalar(path,"SELECT bytes FROM journal_usage")==usage&&Encoding.UTF8.GetString(journal.ReadState("item"))=="old","Quota failure changed authoritative state or usage");
        journal.Execute("owner","after-quota",B("after"),tx=>{tx.WriteState("item",B("new"));return B("good");});
        SqliteOperationJournal.OpenExisting(path);
        using(var db=new SqliteConnection("Data Source="+path)){db.Open();using var q=db.CreateCommand();q.CommandText="DROP TRIGGER usage_state_insert";q.ExecuteNonQuery();}
        Throws<InvalidDataException>(()=>SqliteOperationJournal.OpenExisting(path));
    }
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--child")
        {
            var child = SqliteOperationJournal.OpenExisting(args[1]);
            child.Execute("synthetic-A", "crash-" + args[2], B("request"), tx =>
            {
                tx.WriteState("crash-" + args[2], B("committed"));
                tx.Enqueue("crash-" + args[2], "synthetic-B", B("payload"));
                if (args[2] == "before") { File.WriteAllText(args[1] + ".barrier", "before"); Thread.Sleep(Timeout.Infinite); }
                return B("original-result");
            });
            File.WriteAllText(args[1] + ".barrier", "after"); Thread.Sleep(Timeout.Infinite); return;
        }
        string root = Path.Combine(Path.GetTempPath(), "phinix-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UsageMigrationAndBudget(Path.Combine(root,"usage.sqlite3"));
            PendingPages(Path.Combine(root, "pending-pages.sqlite3"));
            string path = Path.Combine(root, "ledger.sqlite3");
            Throws<IOException>(() => SqliteOperationJournal.OpenExisting(path));
            Check(!File.Exists(path), "Missing ledger silently created");
            var journal = SqliteOperationJournal.CreateNew(path);
            Throws<IOException>(() => SqliteOperationJournal.CreateNew(path));
            JournalTransaction escaped = null;
            var original = journal.Execute("synthetic-A", "op-1", B("intent-v1"), tx =>
            {
                escaped = tx; tx.WriteState("trade-1", B("accepted-both")); tx.Enqueue("delivery-B", "synthetic-B", B("asset-bytes"));
                return B("committed-result");
            });
            Check(!original.Replayed && Encoding.UTF8.GetString(original.Result) == "committed-result", "Commit result");
            Throws<InvalidOperationException>(() => escaped.WriteState("late", B("bad")));
            var replay = SqliteOperationJournal.OpenExisting(path).Execute("synthetic-A", "op-1", B("intent-v1"), _ => throw new Exception("Replay called business"));
            Check(replay.Replayed && Encoding.UTF8.GetString(replay.Result) == "committed-result", "Reopened result lost");
            Throws<OperationConflictException>(() => journal.Execute("synthetic-A", "op-1", B("different"), _ => throw new Exception("Conflict invoked")));
            Throws<ArgumentException>(() => journal.Execute("synthetic-A", "rollback", B("intent"), tx =>
            { tx.WriteState("rollback", B("bad")); tx.Enqueue("rollback", "synthetic-B", B("bad")); return null; }));
            Check((long)Scalar(path, "SELECT count(*) FROM operations") == 1 && (long)Scalar(path, "SELECT count(*) FROM state WHERE id='rollback'") == 0 &&
                (long)Scalar(path, "SELECT count(*) FROM outbox WHERE delivery='rollback'") == 0, "Rollback leaked partial state");
            journal.Execute("synthetic-B", "op-1", B("other-principal"), _ => B("separate"));
            Throws<InvalidOperationException>(() => journal.Execute("synthetic-A", "over-budget", B("intent"), tx =>
            {
                for (int i = 0; i < 129; i++) { try { tx.WriteState("over-budget", B("bad")); } catch (InvalidOperationException) { } }
                return B("must-not-commit");
            }));
            Check((long)Scalar(path, "SELECT count(*) FROM state WHERE id='over-budget'") == 0, "Caught budget failure committed partial work");
            Throws<InvalidOperationException>(() => journal.Execute("synthetic-A", "oversize", B("intent"), tx =>
            {
                tx.WriteState("oversize", B("bad"));
                try { tx.Enqueue("oversize", "synthetic-B", new byte[4 * 1024 * 1024 + 1]); } catch (ArgumentException) { }
                return B("must-not-commit");
            }));
            Check((long)Scalar(path, "SELECT count(*) FROM state WHERE id='oversize'") == 0, "Caught value limit committed partial work");
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var task = Task.Run(() => journal.Execute("synthetic-A", "held", B("held"), tx =>
                { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); return B("held"); }));
                Check(entered.Wait(5000), "Writer barrier");
                try { Throws<JournalBusyException>(() => journal.Execute("synthetic-A", "competing", B("x"), _ => B("bad"))); }
                finally { release.Set(); }
                Check(task.Wait(5000), "Writer drain");
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open(); using var held = connection.BeginTransaction(deferred: false);
                var timer = Stopwatch.StartNew();
                Throws<JournalBusyException>(() => journal.Execute("synthetic-A", "database-busy", B("x"), _ => B("bad")));
                Check(timer.ElapsedMilliseconds < 4000, "Busy writer unbounded");
            }
            Check(!journal.Execute("synthetic-A", "after-busy", B("x"), _ => B("ok")).Replayed, "Busy latched healthy journal");
            string backup = Path.Combine(root, "backup.sqlite3"); journal.BackupToNewFile(backup);
            var restored = SqliteOperationJournal.OpenExisting(backup).Execute("synthetic-A", "op-1", B("intent-v1"), _ => throw new Exception("Backup lost result"));
            Check(restored.Replayed && (long)Scalar(backup, "SELECT count(*) FROM outbox WHERE delivery='delivery-B' AND recipient='synthetic-B' AND status='Pending'") == 1, "Backup lost outbox identity");
            if (args.Length == 2 && args[0] == "--oracle") journal.BackupToNewFile(args[1]);
            foreach (string point in new[] { "before", "after" }) Crash(root, point);
            string invalid = Path.Combine(root, "invalid.sqlite3"); File.WriteAllText(invalid, "synthetic invalid database");
            var bytes = File.ReadAllBytes(invalid); Throws<SqliteException>(() => SqliteOperationJournal.OpenExisting(invalid));
            Check(Convert.ToHexString(bytes) == Convert.ToHexString(File.ReadAllBytes(invalid)), "Invalid file changed");
            string future = Path.Combine(root, "future.sqlite3"); journal.BackupToNewFile(future);
            using (var connection = new SqliteConnection("Data Source=" + future))
            { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=3"; command.ExecuteNonQuery(); }
            Throws<InvalidDataException>(() => SqliteOperationJournal.OpenExisting(future));
            Throws<ArgumentException>(() => new SqliteOperationJournalFactory(root).OpenExisting("../other"));
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "New journal not private");
            Console.WriteLine("JOURNAL RESULT passed; real-file/atomic-state-result-outbox/replay/conflict/rollback/busy/backup/schema/kill-before/kill-after; no game evidence");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void PendingPages(string path)
    {
        var journal = SqliteOperationJournal.CreateNew(path);
        journal.Execute("synthetic-A", "pages", B("pages"), tx =>
        {
            foreach (string id in new[] { "09", "01", "02", "03" }) tx.Enqueue(id, "B", B("evidence-" + id));
            tx.Enqueue("other", "C", B("private")); return B("ok");
        });
        Check(journal.ReadPendingPage("B", null, 2)[0].DeliveryId == "01" && journal.ReadPendingPage("B", "02", 2)[1].DeliveryId == "09", "SQL page cursor/order incorrect");
        Check(journal.ReadPendingPage("b", null, 2).Length == 0 && journal.ReadPendingPage("B", "09", 2).Length == 0, "SQL recipient/cursor isolation");
        Throws<ArgumentOutOfRangeException>(() => journal.ReadPendingPage("B", null, 65));
        Throws<InvalidDataException>(() => journal.Execute("A", "wrong-recipient", B("x"), tx => { tx.RemovePending("01", "C", B("evidence-01")); return B("bad"); }));
        Throws<InvalidDataException>(() => journal.Execute("A", "wrong-evidence", B("x"), tx => { tx.RemovePending("01", "B", B("different")); return B("bad"); }));
        Throws<ArgumentException>(() => journal.Execute("A", "remove-rollback", B("x"), tx => { tx.RemovePending("01", "B", B("evidence-01")); return null; }));
        Check(journal.PendingCount() == 5, "Rejected removal consumed pending evidence");
        journal.Execute("A", "remove", B("x"), tx => { tx.RemovePending("01", "B", B("evidence-01")); return B("unknown-recorded-by-application"); });
        Check(journal.PendingCount() == 4 && journal.ReadPendingPage("B", null, 1)[0].DeliveryId == "02", "Committed removal not visible");
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT delivery,recipient,payload FROM outbox WHERE recipient='B' AND delivery>'01' ORDER BY delivery LIMIT 8";
            using var reader = command.ExecuteReader(); Check(reader.Read() && reader.GetString(3).Contains("outbox_recipient_delivery") && !reader.GetString(3).Contains("SCAN"), "Recovery query does not use recipient cursor index");
        }
        string backup = path + ".backup"; journal.BackupToNewFile(backup);
        Check(SqliteOperationJournal.OpenExisting(backup).ReadPendingPage("B", null, 64).Length == 3, "Backup lost indexed pending rows");
    }
    private static void Crash(string root, string point)
    {
        string path = Path.Combine(root, "crash-" + point + ".sqlite3"); SqliteOperationJournal.CreateNew(path);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--child"); start.ArgumentList.Add(path); start.ArgumentList.Add(point);
        using var process = Process.Start(start)!;
        try
        {
            Check(SpinWait.SpinUntil(() => File.Exists(path + ".barrier") || process.HasExited, 10000) && !process.HasExited, "Child barrier failed");
            process.Kill(entireProcessTree: true); Check(process.WaitForExit(5000), "Kill deadline");
            var reopened = SqliteOperationJournal.OpenExisting(path);
            if (point == "before")
            {
                Check((long)Scalar(path, "SELECT count(*) FROM state") == 0 && (long)Scalar(path, "SELECT count(*) FROM outbox") == 0 && (long)Scalar(path, "SELECT count(*) FROM operations") == 0, "Killed uncommitted transaction survived");
            }
            else
            {
                var result = reopened.Execute("synthetic-A", "crash-after", B("request"), _ => throw new Exception("Committed operation lost after kill"));
                Check(result.Replayed && Encoding.UTF8.GetString(result.Result) == "original-result" && (long)Scalar(path, "SELECT count(*) FROM outbox") == 1 &&
                    Encoding.UTF8.GetString((byte[])Scalar(path, "SELECT value FROM state WHERE id='crash-after'")) == "committed", "Committed kill recovery mismatch");
            }
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
    }
}
