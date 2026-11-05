using System;
using System.IO;

namespace ServerStorage
{
    // Same-directory publication of a complete file. Not a multi-file/DB transaction or power-loss guarantee.
    public static class AtomicFileSnapshot
    {
        public static void Write(string path, Action<Stream> serialize)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentNullException.ThrowIfNull(serialize);
            string destination = Path.GetFullPath(path);
            UnixFileMode? originalMode = null;
            if (Path.Exists(destination))
            {
                var attributes = File.GetAttributes(destination);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new IOException("Snapshot destination must be a regular file.");
                if (!OperatingSystem.IsWindows()) originalMode = File.GetUnixFileMode(destination);
            }
            string temporary = Path.Combine(Path.GetDirectoryName(destination), "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    serialize(stream);
                    stream.Flush(flushToDisk: true);
                }
                if (originalMode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, originalMode.Value);
                // Never delete/truncate the old snapshot before publication; no copy fallback across volumes.
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                // A killed process can leave a private orphan; it is never automatically loaded/promoted.
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
