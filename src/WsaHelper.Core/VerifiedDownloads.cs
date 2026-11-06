using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using System.IO.Compression;

namespace WsaHelper.Core;

public sealed class VerifiedDownloads(HttpClient http)
{
    public async Task<string> GetAsync(Artifact artifact, string cache, IProgress<string> progress, CancellationToken ct)
    {
        ValidateArtifact(artifact);
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, artifact.Sha256 + Path.GetExtension(new Uri(artifact.Url).AbsolutePath));
        if (File.Exists(file) && await VerifyAsync(file, artifact, ct)) return file;
        var partial = file + ".partial";
        try
        {
            using var response = await http.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var final = response.RequestMessage?.RequestUri;
            if (final is null || final.Scheme != "https" || final.Host is not ("github.com" or "release-assets.githubusercontent.com" or "dl.google.com")) throw new InvalidDataException("Download redirect is outside the trusted source hosts.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; long total = 0; var last = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > artifact.Size) throw new InvalidDataException("Download exceeds its pinned size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    var percent = (int)(total * 100 / artifact.Size);
                    if (percent != last) { progress.Report($"Downloading {Path.GetFileName(new Uri(artifact.Url).LocalPath)} — {percent}%"); last = percent; }
                }
            }
            if (!await VerifyAsync(partial, artifact, ct)) throw new InvalidDataException("SHA-256/size verification failed. The download was rejected.");
            File.Move(partial, file, true);
            return file;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    public static void ValidateArtifact(Artifact artifact)
    {
        var uri = new Uri(artifact.Url);
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
            !(uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/MustardChef/WSABuilds/releases/download/", StringComparison.Ordinal) ||
              uri.Host == "dl.google.com" && uri.AbsolutePath.StartsWith("/android/repository/", StringComparison.Ordinal)) ||
            artifact.Size <= 0 || artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Download is outside the pinned source policy.");
    }
    public static async Task<bool> VerifyAsync(string file, Artifact artifact, CancellationToken ct = default)
    {
        if (new FileInfo(file).Length != artifact.Size) return false;
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public static void Extract(string archive, string destination, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        long total = 0; int entries = 0;
        void Copy(string key, long size, bool directory, bool link, Func<Stream> open)
        {
            ct.ThrowIfCancellationRequested();
            if (link || ++entries > 100000 || size < 0 || (total += size) > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Archive exceeds extraction limits or contains a link.");
            var path = Safety.Within(destination, key);
            if (directory) { Directory.CreateDirectory(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = open(); using var output = new FileStream(path, FileMode.CreateNew);
            var buffer = new byte[131072]; long written = 0; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested(); written += count;
                if (written > size) throw new InvalidDataException("Archive entry exceeds declared size.");
                output.Write(buffer, 0, count);
            }
            if (written != size) throw new InvalidDataException("Truncated archive entry.");
        }
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var e in zip.Entries) Copy(e.FullName, e.Length, e.FullName.EndsWith('/'),
                ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000, e.Open);
        }
        else
        {
            using var sevenZip = SevenZipArchive.OpenArchive(archive);
            using var reader = sevenZip.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var e = reader.Entry;
                Copy(e.Key ?? "", e.Size, e.IsDirectory, !string.IsNullOrEmpty(e.LinkTarget), reader.OpenEntryStream);
            }
        }
    }
}
