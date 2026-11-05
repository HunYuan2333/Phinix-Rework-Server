using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authentication;
using Microsoft.Data.Sqlite;
using ServerStorage;

internal static class Program
{
    static int checks;
    static string Id()=>Guid.NewGuid().ToString("D");
    static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    static void Refuses<T>(Action act,string why)where T:Exception{try{act();}catch(T){checks++;return;}throw new Exception(why);}
    static long Count(string path,string table)
    {
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());c.Open();
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM "+table;return (long)cmd.ExecuteScalar()!;
    }
    static string Scalar(string path,string sql)
    {
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());c.Open();
        using var cmd=c.CreateCommand();cmd.CommandText=sql;return (string)cmd.ExecuteScalar()!;
    }
    static void Sql(string path,string sql)
    {
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());c.Open();
        using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();
    }
    static void FaultAndCorruption(string root)
    {
        string path=Path.Combine(root,"fault.sqlite3");var store=PasswordlessIdentityStore.CreateNew(path);
        byte[] first=RandomNumberGenerator.GetBytes(32),second=RandomNumberGenerator.GetBytes(32);
        string credential=Id(),next=Id();var registered=store.Register(Id(),credential,first);var facts=store.Authenticate(credential,first)!;
        const string faultOperation="00000000-0000-4000-8000-000000000001";
        // Deterministic test-only SQLite trigger: abort after authority writes, before the operation result insert.
        Sql(path,"CREATE TRIGGER identity_before_result BEFORE INSERT ON operations WHEN NEW.operation='"+faultOperation+"' BEGIN SELECT RAISE(ABORT,'synthetic.identity.before-result'); END");
        Refuses<IOException>(()=>store.RotateAuthorizedCredential(faultOperation,registered.PrincipalId,credential,next,second),"Injected pre-result failure was reported as success");
        Check(Count(path,"operations")==2 && Count(path,"outbox")==1,"Aborted rotation left a result or pending event");
        Check(store.Authenticate(credential,first)!=null && store.Authenticate(next,second)==null && store.IsCurrent(facts),"Aborted transaction changed credential ownership");
        Sql(path,"DROP TRIGGER identity_before_result");store=PasswordlessIdentityStore.OpenExisting(path);
        var rotated=store.RotateAuthorizedCredential(faultOperation,registered.PrincipalId,credential,next,second);
        Check(rotated.Epoch==2 && store.Authenticate(credential,first)==null && store.Authenticate(next,second)!=null,"Explicit reopen/same operation did not recover a controlled rollback");
        Check(Count(path,"operations")==3 && Count(path,"outbox")==2,"Recovered operation did not commit state/result/event exactly once");
        Sql(path,"UPDATE state SET value=CAST(json_remove(CAST(value AS TEXT),'$.Revoked') AS BLOB) WHERE id='identity.credential/"+next+"'");
        Refuses<InvalidDataException>(()=>store.Authenticate(next,second),"Missing revoked field silently defaulted to active");
        string incomplete=Path.Combine(root,"incomplete.sqlite3");SqliteOperationJournal.CreateNew(incomplete);
        Refuses<InvalidDataException>(()=>PasswordlessIdentityStore.OpenExisting(incomplete),"Incomplete identity bootstrap was silently recreated");
        NoSecretFiles(root,first,second);
    }
    static void ConcurrentRotation(string root)
    {
        string path=Path.Combine(root,"concurrent.sqlite3");var store=PasswordlessIdentityStore.CreateNew(path);
        byte[] original=RandomNumberGenerator.GetBytes(32);string old=Id();var created=store.Register(Id(),old,original);
        var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] credentials={Id(),Id()},operations={"00000000-0000-4000-8000-000000000010","00000000-0000-4000-8000-000000000011"};
        byte[][] secrets={RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32)};
        var independent=new[]{PasswordlessIdentityStore.OpenExisting(path),PasswordlessIdentityStore.OpenExisting(path)};
        Task<PasswordlessIdentityStore.MutationResult?>[] attempts=Enumerable.Range(0,2).Select(i=>Task.Run(async ()=>{
            await start.Task;
            try{return independent[i].RotateAuthorizedCredential(operations[i],created.PrincipalId,old,credentials[i],secrets[i]);}
            catch(InvalidOperationException){return null;}catch(JournalBusyException){return null;}
        })).ToArray();
        start.TrySetResult();Task.WaitAll(attempts);
        Check(attempts.Count(t=>t.Result!=null)==1,"Concurrent independent writers accepted two active replacement credentials");
        int winner=Array.FindIndex(attempts,t=>t.Result!=null),loser=1-winner;
        var reopened=PasswordlessIdentityStore.OpenExisting(path);
        Check(reopened.Authenticate(old,original)==null && reopened.Authenticate(credentials[winner],secrets[winner])?.Epoch==2 &&
            reopened.Authenticate(credentials[loser],secrets[loser])==null,"Concurrent rotation state differs from its single committed result");
        Check(Count(path,"operations")==3 && Count(path,"outbox")==2,"Concurrent rejected operation left a result/event");
        Check(reopened.RotateAuthorizedCredential(operations[winner],created.PrincipalId,old,credentials[winner],secrets[winner])==attempts[winner].Result,
            "Winning concurrent operation did not replay its original result");
        NoSecretFiles(root,original,secrets[0],secrets[1]);
    }
    static void NoSecretFiles(string root,params byte[][] secrets)
    {
        foreach(string file in Directory.GetFiles(root))
        {
            byte[] bytes=File.ReadAllBytes(file);string text=Encoding.UTF8.GetString(bytes);
            foreach(byte[] secret in secrets)Check(bytes.AsSpan().IndexOf(secret)<0 && !text.Contains(Convert.ToBase64String(secret)) && !text.Contains(Convert.ToHexString(secret)),"Identity storage/result file contains a plaintext credential");
        }
    }
    static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"phinix-identity-"+Id());Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"identity.sqlite3");var store=PasswordlessIdentityStore.CreateNew(path);
            byte[] secret=RandomNumberGenerator.GetBytes(32),nextSecret=RandomNumberGenerator.GetBytes(32),wrong=RandomNumberGenerator.GetBytes(32);
            string credential=Id(),operation=Id();var registered=store.Register(operation,credential,secret);
            Check(registered.PrincipalId!=credential && registered.Epoch==1,"Stable principal was conflated with credential ID");
            var facts=store.Authenticate(credential,secret);Check(facts!=null && store.IsCurrent(facts),"Committed credential did not authenticate");
            Check(store.Authenticate(credential,wrong)==null && store.Authenticate(Id(),secret)==null,"Wrong/unknown credential was accepted");
            Check(store.Register(operation,credential,secret)==registered,"Registration replay changed original principal/result");
            Refuses<OperationConflictException>(()=>store.Register(operation,credential,nextSecret),"Conflicting registration operation was accepted");
            Refuses<InvalidOperationException>(()=>store.Register(Id(),credential,secret),"Credential ID could be claimed twice");
            Check(Count(path,"operations")==2 && Count(path,"outbox")==1,"Registration result/state/outbox were not committed once");
            var reopened=PasswordlessIdentityStore.OpenExisting(path);
            Check(reopened.ServerId==store.ServerId && reopened.Register(operation,credential,secret)==registered,"Restart lost server identity or original result");
            string secondPath=Path.Combine(root,"second.sqlite3");var other=PasswordlessIdentityStore.CreateNew(secondPath);
            other.BindAuthorizedPrincipal(Id(),registered.PrincipalId,credential,secret);
            string hash1=Scalar(path,"SELECT json_extract(CAST(value AS TEXT),'$.Verifier') FROM state WHERE id LIKE 'identity.credential/%'");
            string hash2=Scalar(secondPath,"SELECT json_extract(CAST(value AS TEXT),'$.Verifier') FROM state WHERE id LIKE 'identity.credential/%'");
            Check(hash1!=hash2,"Verifier is not separated by stable server identity");
            Check(store.Authenticate(credential,Convert.FromBase64String(hash1))==null,"Stored verifier was accepted as a bearer secret");
            Check(!other.IsCurrent(facts!),"Authentication facts from another server identity were accepted");
            // UUID binding preserves the exact pre-existing player identifier, including its stored case.
            string oldPrincipal=Id().ToUpperInvariant(),boundCredential=Id(),binding=Id();
            var bound=store.BindAuthorizedPrincipal(binding,oldPrincipal,boundCredential,secret);
            Check(bound.PrincipalId==oldPrincipal && store.Authenticate(boundCredential,secret)!.PrincipalId==oldPrincipal,"Binding rewrote original player UUID");
            Check(store.BindAuthorizedPrincipal(binding,oldPrincipal,boundCredential,secret)==bound,"Binding replay did not retain original result");
            Refuses<InvalidOperationException>(()=>store.BindAuthorizedPrincipal(Id(),oldPrincipal,Id(),secret),"Existing principal could be rebound without rotation");
            string nextCredential=Id(),rotation=Id();
            long operationsBefore=Count(path,"operations"),eventsBefore=Count(path,"outbox");
            Refuses<InvalidOperationException>(()=>store.RotateAuthorizedCredential(Id(),oldPrincipal,credential,nextCredential,nextSecret),"Cross-owner rotation succeeded");
            Check(Count(path,"operations")==operationsBefore && Count(path,"outbox")==eventsBefore && store.IsCurrent(facts!),"Rejected rotation changed authority/result/outbox");
            var rotated=store.RotateAuthorizedCredential(rotation,registered.PrincipalId,credential,nextCredential,nextSecret);
            Check(rotated.PrincipalId==registered.PrincipalId && rotated.Epoch==2,"Rotation recreated principal or failed to advance epoch");
            Check(store.Authenticate(credential,secret)==null && !store.IsCurrent(facts!),"Rotation left old credential/session facts active");
            var nextFacts=store.Authenticate(nextCredential,nextSecret);Check(nextFacts!=null && store.IsCurrent(nextFacts),"Rotated credential did not become active");
            Check(store.RotateAuthorizedCredential(rotation,registered.PrincipalId,credential,nextCredential,nextSecret)==rotated,"Lost rotation response changed result on replay");
            Refuses<OperationConflictException>(()=>store.RotateAuthorizedCredential(rotation,registered.PrincipalId,credential,Id(),nextSecret),"Conflicting rotation replay was accepted");
            string revoke=Id();var revoked=store.RevokeAuthorizedCredential(revoke,registered.PrincipalId,nextCredential);
            Check(revoked.Epoch==3 && store.Authenticate(nextCredential,nextSecret)==null && !store.IsCurrent(nextFacts!),"Revocation did not invalidate active credential/session facts");
            store=PasswordlessIdentityStore.OpenExisting(path);
            Check(store.RevokeAuthorizedCredential(revoke,registered.PrincipalId,nextCredential)==revoked && store.Register(operation,credential,secret)==registered,"Later revocation overwrote original historical operation results");
            Check(store.Authenticate(credential,secret)==null && store.Authenticate(nextCredential,nextSecret)==null,"Restart resurrected revoked credentials");
            Check(Count(path,"outbox")==4,"Atomic change events duplicated or missing");
            Refuses<ArgumentException>(()=>store.Register(Id(),Id(),new byte[16]),"Low-size credential accepted");
            Refuses<ArgumentException>(()=>store.Register("not-an-operation",Id(),secret),"Invalid operation ID accepted");
            Refuses<IOException>(()=>PasswordlessIdentityStore.CreateNew(path),"CreateNew replaced existing identity database");
            Refuses<IOException>(()=>PasswordlessIdentityStore.OpenExisting(Path.Combine(root,"missing.sqlite3")),"Open silently recreated missing identity data");
            long beforeQuery = Count(path,"operations");
            Check(store.QueryOwnRevocation(revoke,nextCredential,nextSecret) == revoked,"Committed revocation result could not be read with its original proof");
            Check(store.QueryOwnRevocation(Id(),nextCredential,nextSecret) == null,"Missing revocation query invented a result");
            Refuses<InvalidOperationException>(()=>store.QueryOwnRevocation(revoke,nextCredential,wrong),"Revocation query accepted another proof");
            Refuses<OperationConflictException>(()=>store.QueryOwnRevocation(revoke,credential,secret),"Revocation result escaped its credential owner");
            Check(Count(path,"operations")==beforeQuery && store.Authenticate(nextCredential,nextSecret)==null,"Read-only result query mutated state or restored authentication");
            NoSecretFiles(root,secret,nextSecret,wrong);
            FaultAndCorruption(root);
            ConcurrentRotation(root);
            Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks,scope="SV-41 identity SQLite component; no network exposure",secretsInOutput=false,existingPlayersTouched=false}));
        }
        finally {Directory.Delete(root,true);}
    }
}
