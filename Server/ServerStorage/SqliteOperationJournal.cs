using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace ServerStorage;

public interface IOperationJournalFactory
{
    SqliteOperationJournal CreateNew(string name);
    SqliteOperationJournal OpenExisting(string name);
}

// Trusted plugins own their namespaces, as with the existing storage provider. Not a sandbox.
public sealed class SqliteOperationJournalFactory(string root) : IOperationJournalFactory
{
    private readonly string root = Path.GetFullPath(root);
    private string FilePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Contains("..")) throw new ArgumentException("Invalid journal name.");
        foreach (char c in name) if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_') throw new ArgumentException("Invalid journal name.");
        return Path.Combine(root, name + ".sqlite3");
    }
    public SqliteOperationJournal CreateNew(string name) => SqliteOperationJournal.CreateNew(FilePath(name));
    public SqliteOperationJournal OpenExisting(string name) => SqliteOperationJournal.OpenExisting(FilePath(name));
}

public sealed class OperationConflictException() : Exception("Operation ID has a different request fingerprint.");
public sealed class JournalCapacityException() : Exception("Journal storage budget exhausted; no new operation was committed.");
public sealed class JournalBusyException() : Exception("Journal is busy; no committed result was returned.");
public sealed class CommittedOperation
{
    private readonly byte[] result;
    public byte[] Result => (byte[])result.Clone();
    public bool Replayed { get; }
    internal CommittedOperation(byte[] result, bool replayed) { this.result = (byte[])result.Clone(); Replayed = replayed; }
}

public sealed record PendingJournalEntry(string DeliveryId, string Recipient, byte[] Payload);

// Opaque application bytes: no wire profiles, credentials, game rules or network callbacks.
// Return from Execute is the only commit boundary. Callback code must not send or mutate external state.
public sealed class SqliteOperationJournal
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    private const long ApplicationId = 0x50484e58;
    private readonly string path;
    private readonly object writer = new();
    private bool writeFaulted;
    private SqliteOperationJournal(string path) { this.path = Path.GetFullPath(path); }

    public static SqliteOperationJournal CreateNew(string path)
    {
        var journal = new SqliteOperationJournal(path);
        Directory.CreateDirectory(Path.GetDirectoryName(journal.path)!);
        // Never replace a file or silently create a missing authoritative journal on normal open.
        CreatePrivateFile(journal.path);
        using var connection = journal.Connect();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE state (id TEXT PRIMARY KEY, value BLOB NOT NULL) STRICT;
            CREATE TABLE operations (principal TEXT NOT NULL, operation TEXT NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32), result BLOB NOT NULL,
                PRIMARY KEY(principal, operation)) STRICT;
            CREATE TABLE outbox (delivery TEXT PRIMARY KEY, recipient TEXT NOT NULL, payload BLOB NOT NULL, status TEXT NOT NULL CHECK(status='Pending')) STRICT;
            CREATE INDEX outbox_recipient_delivery ON outbox(recipient,delivery);
            PRAGMA application_id=1346915928;
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        journal.UpgradeSchema();journal.Validate();
        return journal;
    }

    public static SqliteOperationJournal OpenExisting(string path)
    {
        var journal = new SqliteOperationJournal(path);
        journal.Validate();journal.UpgradeSchema();journal.Validate();
        // Additive access path only: no data/schema-version conversion or record eviction.
        using var connection = journal.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS outbox_recipient_delivery ON outbox(recipient,delivery)";
        command.ExecuteNonQuery();
        return journal;
    }

    private void UpgradeSchema()
    {
        using var connection=Connect();using var tx=connection.BeginTransaction(deferred:false);using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="PRAGMA user_version";if(Convert.ToInt64(command.ExecuteScalar())==2)return;
        command.CommandText="CREATE TABLE journal_usage (singleton INTEGER PRIMARY KEY CHECK(singleton=1),bytes INTEGER NOT NULL CHECK(bytes>=0)) STRICT; INSERT INTO journal_usage VALUES(1,0);";command.ExecuteNonQuery();
        foreach(var table in new[]{("state","length(CAST(id AS BLOB))+length(value)"),("operations","length(CAST(principal AS BLOB))+length(CAST(operation AS BLOB))+length(fingerprint)+length(result)"),("outbox","length(CAST(delivery AS BLOB))+length(CAST(recipient AS BLOB))+length(payload)")})
        {
            string N(string expression,string prefix)=>System.Text.RegularExpressions.Regex.Replace(expression,@"\b(id|value|principal|operation|fingerprint|result|delivery|recipient|payload)\b",m=>prefix+"."+m.Value);
            command.CommandText=$"UPDATE journal_usage SET bytes=bytes+(SELECT coalesce(sum({table.Item2}),0) FROM {table.Item1});";command.ExecuteNonQuery();
            foreach(string action in new[]{"INSERT","UPDATE","DELETE"})
            {
                string delta=action=="INSERT"?N(table.Item2,"new"):action=="DELETE"?"-("+N(table.Item2,"old")+")":"("+N(table.Item2,"new")+")-("+N(table.Item2,"old")+")";
                command.CommandText=$"CREATE TRIGGER usage_{table.Item1}_{action.ToLowerInvariant()} AFTER {action} ON {table.Item1} BEGIN UPDATE journal_usage SET bytes=bytes+({delta}) WHERE singleton=1; END;";command.ExecuteNonQuery();
            }
        }
        command.CommandText="PRAGMA user_version=2";command.ExecuteNonQuery();tx.Commit();
    }

    private SqliteConnection Connect()
    {
        if (!File.Exists(path) || new FileInfo(path).LinkTarget != null) throw new IOException("Journal must be an existing regular file.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Rollback journal deliberately avoids an unconfigured WAL/checkpoint lifecycle in this first slice.
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 5 || error.SqliteErrorCode == 6)
        { connection.Dispose(); throw new JournalBusyException(); }
        catch { connection.Dispose(); throw; }
    }

    private void Validate()
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id";
        if (Convert.ToInt64(command.ExecuteScalar()) != ApplicationId) throw new InvalidDataException("Unrecognized journal application.");
        command.CommandText = "PRAGMA user_version";
        long version=Convert.ToInt64(command.ExecuteScalar());
        if(version!=1&&version!=2)throw new InvalidDataException("Unsupported journal schema.");
        command.CommandText = "PRAGMA quick_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new InvalidDataException("Journal integrity check failed.");
        command.CommandText = "SELECT id,value FROM state LIMIT 0; SELECT principal,operation,fingerprint,result FROM operations LIMIT 0; SELECT delivery,recipient,payload,status FROM outbox LIMIT 0;";
        command.ExecuteNonQuery();
        if(version==2)
        {
            command.CommandText="SELECT bytes FROM journal_usage WHERE singleton=1";long usage=Convert.ToInt64(command.ExecuteScalar());
            command.CommandText="SELECT (SELECT coalesce(sum(length(CAST(id AS BLOB))+length(value)),0) FROM state)+(SELECT coalesce(sum(length(CAST(principal AS BLOB))+length(CAST(operation AS BLOB))+length(fingerprint)+length(result)),0) FROM operations)+(SELECT coalesce(sum(length(CAST(delivery AS BLOB))+length(CAST(recipient AS BLOB))+length(payload)),0) FROM outbox)";
            if(usage!=Convert.ToInt64(command.ExecuteScalar()))throw new InvalidDataException("Journal usage evidence differs from authoritative rows.");
            foreach(var table in new[]{"state","operations","outbox"})foreach(var action in new[]{"insert","update","delete"})
            {
                command.CommandText="SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name=$name AND tbl_name=$table";
                command.Parameters.Clear();command.Parameters.AddWithValue("$name","usage_"+table+"_"+action);command.Parameters.AddWithValue("$table",table);
                if(Convert.ToInt64(command.ExecuteScalar())!=1)throw new InvalidDataException("Journal usage trigger missing.");
            }
        }
    }

    public CommittedOperation Execute(string principal, string operationId, byte[] canonicalRequest, Func<JournalTransaction, byte[]> apply,long maxDatabaseBytes=0)
    {
        Id(principal); Id(operationId); Bytes(canonicalRequest); ArgumentNullException.ThrowIfNull(apply);
        byte[] fingerprint = SHA256.HashData(canonicalRequest);
        // No unbounded queue, no implicit application retry. Re-entry also cannot nest transactions.
        if (Monitor.IsEntered(writer) || !Monitor.TryEnter(writer)) throw new JournalBusyException();
        try
        {
            if (writeFaulted) throw new IOException("Journal writes disabled after a storage failure; reopen after inspection.");
            try
            {
                using var connection = Connect();
                using var transaction = connection.BeginTransaction(deferred: false);
                using var query = connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "SELECT fingerprint,result FROM operations WHERE principal=$principal AND operation=$operation";
                query.Parameters.AddWithValue("$principal", principal); query.Parameters.AddWithValue("$operation", operationId);
                using (var reader = query.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        if (!CryptographicOperations.FixedTimeEquals((byte[])reader[0], fingerprint)) throw new OperationConflictException();
                        return new CommittedOperation((byte[])reader[1], true);
                    }
                }
                if(maxDatabaseBytes>0)
                {
                    using var budget=connection.CreateCommand();budget.Transaction=transaction;budget.CommandText="PRAGMA page_size";long size=(long)budget.ExecuteScalar();
                    budget.CommandText="PRAGMA max_page_count="+(maxDatabaseBytes/size).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if((long)budget.ExecuteScalar()*size>maxDatabaseBytes)throw new JournalCapacityException();
                }
                var scope = new JournalTransaction(connection, transaction);
                byte[] result;
                try { result = apply(scope); scope.EnsureCommittable(); Bytes(result); result = (byte[])result.Clone(); }
                finally { scope.Close(); }
                query.CommandText = "INSERT INTO operations(principal,operation,fingerprint,result) VALUES($principal,$operation,$fingerprint,$result)";
                query.Parameters.AddWithValue("$fingerprint", fingerprint); query.Parameters.AddWithValue("$result", result);
                query.ExecuteNonQuery();
                transaction.Commit();
                return new CommittedOperation(result, false);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 5 || e.SqliteErrorCode == 6) { throw new JournalBusyException(); }
            catch (SqliteException e) when(e.SqliteErrorCode==13 && maxDatabaseBytes>0) { throw new JournalCapacityException(); }
            catch (SqliteException) { writeFaulted = true; throw new IOException("Journal storage failure; operation outcome must be queried after recovery."); }
            catch (IOException) { writeFaulted = true; throw; }
        }
        finally { Monitor.Exit(writer); }
    }

    public byte[] ReadState(string id)
    {
        Id(id);
        try
        {
            using var connection = Connect(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM state WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as byte[];
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 5 || error.SqliteErrorCode == 6) { throw new JournalBusyException(); }
    }

    // Stable binary-key cursor; exact recipient matching is intentional. Applications
    // normalize their own stable principals before enqueue and lookup.
    public PendingJournalEntry[] ReadPendingPage(string recipient, string afterDeliveryId, int count)
    {
        Id(recipient);
        if (afterDeliveryId != null) Id(afterDeliveryId);
        if (count < 1 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
        try
        {
            using var connection = Connect(); using var command = connection.CreateCommand();
            command.CommandText = afterDeliveryId == null
                ? "SELECT delivery,recipient,payload FROM outbox WHERE recipient=$r ORDER BY delivery LIMIT $n"
                : "SELECT delivery,recipient,payload FROM outbox WHERE recipient=$r AND delivery>$after ORDER BY delivery LIMIT $n";
            command.Parameters.AddWithValue("$r", recipient); command.Parameters.AddWithValue("$n", count);
            if (afterDeliveryId != null) command.Parameters.AddWithValue("$after", afterDeliveryId);
            using var reader = command.ExecuteReader(); var entries = new List<PendingJournalEntry>();
            while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), (byte[])reader[2]));
            return entries.ToArray();
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 5 || error.SqliteErrorCode == 6) { throw new JournalBusyException(); }
    }

    public long PendingCount()
    {
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM outbox"; return (long)command.ExecuteScalar();
    }

    public CommittedOperation TryReadCommitted(string principal, string operationId, byte[] canonicalRequest)
    {
        Bytes(canonicalRequest);
        return TryReadCommittedFingerprint(principal, operationId, SHA256.HashData(canonicalRequest));
    }

    // Read-only lookup of an exact original request; caller supplies the authenticated principal.
    public CommittedOperation TryReadCommittedFingerprint(string principal, string operationId, byte[] fingerprint)
    {
        Id(principal); Id(operationId);
        if (fingerprint == null || fingerprint.Length != 32) throw new ArgumentException("Expected SHA-256 fingerprint.");
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint,result FROM operations WHERE principal=$p AND operation=$o";
        command.Parameters.AddWithValue("$p", principal); command.Parameters.AddWithValue("$o", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!CryptographicOperations.FixedTimeEquals((byte[])reader[0], fingerprint)) throw new OperationConflictException();
        return new CommittedOperation((byte[])reader[1], true);
    }

    public void BackupToNewFile(string destination)
    {
        if (Monitor.IsEntered(writer) || !Monitor.TryEnter(writer)) throw new JournalBusyException();
        try
        {
            var target = new SqliteOperationJournal(destination);
            CreatePrivateFile(target.path);
            using var source = Connect();
            using var backup = target.Connect();
            source.BackupDatabase(backup);
            target.Validate();
        }
        finally { Monitor.Exit(writer); }
    }

    internal static void Id(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains('\0')) throw new ArgumentException("Invalid journal identifier."); }
    internal static void Bytes(byte[] value) { if (value == null || value.Length > MaxBytes) throw new ArgumentException("Journal value exceeds operation limit."); }
    private static void CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
    }
}

// Transaction handles expire before COMMIT/rollback and must remain on the callback thread.
public sealed class JournalTransaction
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool active = true;
    private int mutations, writtenBytes;
    private bool budgetExceeded;
    internal JournalTransaction(SqliteConnection connection, SqliteTransaction transaction) { this.connection = connection; this.transaction = transaction; }
    internal void Close() => active = false;
    internal void EnsureCommittable() { if (budgetExceeded) throw new InvalidOperationException("Transaction mutation budget exceeded."); }
    private void Reserve(byte[] value)
    {
        try { SqliteOperationJournal.Bytes(value); }
        catch (ArgumentException) { budgetExceeded = true; throw; }
        if (++mutations > 128 || (writtenBytes += value.Length) > 8 * 1024 * 1024) budgetExceeded = true;
        EnsureCommittable();
    }
    private SqliteCommand Command(string sql)
    {
        if (!active || thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Transaction handle expired or crossed threads.");
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
    }
    public byte[] ReadState(string id)
    {
        SqliteOperationJournal.Id(id);
        using var command = Command("SELECT value FROM state WHERE id=$id"); command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is byte[] value ? value : null;
    }
    public void EnsureStorageBudget(long limit,long additionalBytes)
    {
        using var command=Command("SELECT bytes FROM journal_usage WHERE singleton=1");
        if((long)command.ExecuteScalar()+additionalBytes>limit)throw new JournalCapacityException();
        command.CommandText="PRAGMA page_count";long pages=(long)command.ExecuteScalar();command.CommandText="PRAGMA page_size";long pageSize=(long)command.ExecuteScalar();
        if(pages*pageSize+additionalBytes>limit)throw new JournalCapacityException();
    }
    public void WriteState(string id, byte[] value)
    {
        SqliteOperationJournal.Id(id); Reserve(value);
        using var command = Command("INSERT INTO state(id,value) VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET value=excluded.value");
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
    }
    public void Enqueue(string deliveryId, string recipient, byte[] payload)
    {
        SqliteOperationJournal.Id(deliveryId); SqliteOperationJournal.Id(recipient); Reserve(payload);
        using var command = Command("INSERT INTO outbox(delivery,recipient,payload,status) VALUES($id,$recipient,$payload,'Pending')");
        command.Parameters.AddWithValue("$id", deliveryId); command.Parameters.AddWithValue("$recipient", recipient); command.Parameters.AddWithValue("$payload", payload); command.ExecuteNonQuery();
    }

    // Removal is a pending-queue transition, not a durable receipt. The application
    // must retain unknown/terminal evidence in its authoritative transaction state.
    public void RemovePending(string deliveryId, string recipient, byte[] expectedPayload)
    {
        SqliteOperationJournal.Id(deliveryId); SqliteOperationJournal.Id(recipient); Reserve(expectedPayload);
        using var command = Command("DELETE FROM outbox WHERE delivery=$id AND recipient=$r AND payload=$p");
        command.Parameters.AddWithValue("$id", deliveryId); command.Parameters.AddWithValue("$r", recipient);
        command.Parameters.AddWithValue("$p", expectedPayload);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Pending evidence does not match.");
    }
}
