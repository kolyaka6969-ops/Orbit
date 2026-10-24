using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Orbit.Core.Backends;

/// <summary>ZIP — встроенными средствами .NET; распаковка ZIP, 7z, RAR, TAR, GZ, BZ2, XZ — через SharpCompress.</summary>
public static class ArchiveBackend
{
    public static Task ZipAsync(IReadOnlyList<FileItem> items, string destination, IProgress<double> progress, CancellationToken token) => Task.Run(() =>
    {
        var entries = new List<(string File, string Entry)>();
        foreach (var item in items)
        {
            if (!item.IsDirectory)
            {
                entries.Add((item.Path, item.Name));
                continue;
            }
            var root = item.Folder;
            foreach (var file in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                entries.Add((file, Path.GetRelativePath(root, file).Replace('\\', '/')));
        }
        var total = Math.Max(1L, entries.Sum(e => new FileInfo(e.File).Length));
        long done = 0;
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var (file, entryName) in entries)
        {
            token.ThrowIfCancellationRequested();
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            done += new FileInfo(file).Length;
            progress.Report((double)done / total);
        }
    }, token);

    /// <summary>Распаковывает в новую папку с именем архива рядом с ним.</summary>
    public static Task<string> ExtractAsync(FileItem item, string outDir, IProgress<double> progress, CancellationToken token) => Task.Run(() =>
    {
        var dir = OutputPaths.UniqueFolder(outDir, item.BaseName);
        Directory.CreateDirectory(dir);
        var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = false };
        try
        {
            if (item.Extension is "7z" or "zip" or "rar")
            {
                // Целиком, а не по одному элементу: в «плотных» 7z и RAR поштучное извлечение очень медленное.
                using var archive = ArchiveFactory.OpenArchive(item.Path);
                archive.WriteToDirectory(dir, options);
            }
            else
            {
                // TAR и сжатые потоки (tar.gz, tar.xz, .gz…) читаются последовательно.
                using var stream = File.OpenRead(item.Path);
                using var reader = ReaderFactory.OpenReader(stream);
                while (reader.MoveToNextEntry())
                {
                    token.ThrowIfCancellationRequested();
                    if (!reader.Entry.IsDirectory)
                        reader.WriteEntryToDirectory(dir, options);
                    progress.Report((double)stream.Position / Math.Max(1, stream.Length));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
            throw ex is CryptographicException or ArchiveException or InvalidFormatException
                ? new InvalidOperationException($"Архив не распакован: {ex.Message}")
                : ex;
        }
        return dir;
    }, token);
}
