using System;
using System.IO;
using System.Text;

namespace Authentication;

public static class IdentityAuthorityBootstrap
{
    public static void PinMode(string path, string serverId)
    {
        if (File.Exists(path))
        {
            if (File.ReadAllText(path) != serverId) throw new InvalidDataException("Identity mode authority differs.");
            return;
        }
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(Encoding.UTF8.GetBytes(serverId)); output.Flush(true);
    }
    public static PasswordlessIdentityStore Open(string path, bool initializeNew)
    {
        path = Path.GetFullPath(path);
        string marker = path + ".identity-initialized";
        if (!File.Exists(path) && (!initializeNew || File.Exists(marker)))
            throw new IOException("Expected identity database is missing; refusing to recreate players.");
        var authority = File.Exists(path) ? PasswordlessIdentityStore.OpenExisting(path) : PasswordlessIdentityStore.CreateNew(path);
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker) != authority.ServerId) throw new InvalidDataException("Identity authority marker differs.");
        }
        else
        {
            using var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(Encoding.UTF8.GetBytes(authority.ServerId)); output.Flush(true);
        }
        return authority;
    }
}
