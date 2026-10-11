// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToyBoxDecryptor.Core;

public sealed record InputFile(string Path, string? Member = null)
{
    public string Key => System.IO.Path.GetFullPath(Path) + "|" + Member;
    public string Name => Member ?? System.IO.Path.GetFileName(Path);
    public string Location => Member == null ? Path : Path + " :: " + Member;
    public string Package => Member == null ? System.IO.Path.GetDirectoryName(Path)! : Path;
    public byte[] Read()
    {
        if (Member == null) {
            using var stream = File.OpenRead(Path);
            return ReadBounded(stream, stream.Length);
        }
        using var zip = ZipFile.OpenRead(Path);
        var entry = zip.GetEntry(Member) ?? throw new FileNotFoundException("ZIP member no longer exists.");
        using var input = entry.Open();
        return ReadBounded(input, entry.Length);
    }
    private static byte[] ReadBounded(Stream stream, long length)
    {
        if (length > SaveCodec.MaxFileSize) throw new InvalidDataException("Save exceeds the 16 MB limit.");
        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Input changed while being read, or its size is invalid.");
        return bytes;
    }
}
public sealed record ScanResult(List<InputFile> Files, List<string> Messages, int Ignored);

public static class InputScanner
{
    public const int MaxInputs = 20000;
    public static ScanResult Scan(IEnumerable<string> paths, CancellationToken token, string? excludeDirectory = null)
    {
        var files = new List<InputFile>();
        var messages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ignored = 0, examined = 0;
        void Add(InputFile file) { if (seen.Add(file.Key)) files.Add(file); }
        bool Excluded(string path) => excludeDirectory != null &&
            IsWithin(path, excludeDirectory);
        void VisitFile(string path, bool explicitFile)
        {
            token.ThrowIfCancellationRequested();
            if (++examined > MaxInputs) throw new InvalidDataException("Scan limit reached (20,000 files). Add smaller batches.");
            if (Excluded(path)) return;
            try {
                using var stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[4];
                int size = stream.Read(header);
                bool isZip = size == 4 && header[0] == 'P' && header[1] == 'K';
                if (isZip || System.IO.Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) {
                    stream.Dispose();
                    if (new FileInfo(path).Length > 512L << 20) throw new InvalidDataException("ZIP exceeds the 512 MB limit.");
                    using var zip = ZipFile.OpenRead(path);
                    if (zip.Entries.Count > MaxInputs) throw new InvalidDataException("ZIP contains more than 20,000 entries.");
                    long total = 0;
                    foreach (var entry in zip.Entries) {
                        token.ThrowIfCancellationRequested();
                        if (entry.FullName.EndsWith('/')) continue;
                        total += entry.Length;
                        if (total > 512L << 20) throw new InvalidDataException("ZIP expanded contents exceed the 512 MB limit.");
                        if (entry.Length is < 4 or > SaveCodec.MaxFileSize) { ignored++; continue; }
                        using var member = entry.Open();
                        byte[] first = new byte[4];
                        member.ReadExactly(first);
                        if (SaveCodec.HasSaveHeader(first)) Add(new(path, entry.FullName));
                        else ignored++;
                    }
                }
                else if (SaveCodec.HasSaveHeader(header[..size]) || explicitFile) Add(new(path));
                else ignored++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
                messages.Add(System.IO.Path.GetFileName(path) + ": " + e.Message);
            }
        }
        void VisitFolder(string folder)
        {
            token.ThrowIfCancellationRequested();
            if (Excluded(folder)) return;
            try {
                foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase)) VisitFile(file, false);
                foreach (string sub in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase)) {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) VisitFolder(sub);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { messages.Add(folder + ": " + e.Message); }
        }
        foreach (string supplied in paths) {
            token.ThrowIfCancellationRequested();
            string path = System.IO.Path.GetFullPath(supplied);
            if (Directory.Exists(path)) VisitFolder(path);
            else if (File.Exists(path)) VisitFile(path, true);
            else messages.Add(path + ": Path does not exist.");
        }
        if (files.Count > MaxInputs) throw new InvalidDataException("More than 20,000 saves found. Add smaller batches.");
        return new(files, messages, ignored);
    }
    public static bool IsWithin(string path, string root)
    {
        string full = System.IO.Path.GetFullPath(path), parent = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ExportOptions(bool Text = true, bool PcSave = true, bool Cmp1 = false, bool ToyBoxOnly = true, GameProfile Profile = GameProfile.Auto);
public sealed record FileResult(string Source, string Status, string Platform, string Type, string World, string Message, string[] Outputs,
    string Game = "Undetermined", string VersionEvidence = "", int? ToyBoxVersion = null, string Compatibility = "");
public sealed record BatchProgress(int Completed, int Total, InputFile Input, FileResult Result);
public sealed record BatchResult(string Directory, List<FileResult> Files, bool Cancelled)
{
    public int Saved => Files.Count(f => f.Status == "Saved");
    public int Failed => Files.Count(f => f.Status == "Failed");
    public int Skipped => Files.Count(f => f.Status == "Skipped");
}

public static class BatchExporter
{
    public static BatchResult Run(IReadOnlyList<InputFile> inputs, string outputRoot, ExportOptions options,
        IProgress<BatchProgress>? progress, CancellationToken token)
    {
        if (!options.Text && !options.PcSave && !options.Cmp1) throw new ArgumentException("Select at least one export format.");
        if (inputs.Count == 0) throw new ArgumentException("Add some save files first.");
        // A unique directory prevents overwrites and never edits an input, even if output and input folders coincide.
        string run = System.IO.Path.Combine(System.IO.Path.GetFullPath(outputRoot), "Export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        var results = new List<FileResult>();
        bool cancelled = false;
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < inputs.Count; i++) {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            InputFile input = inputs[i];
            var created = new List<string>();
            FileResult result;
            try {
                DecodedSave decoded = SaveCodec.Decode(input.Read(), options.Profile, input.Name);
                FileResult Result(string status, string message, string[] outputs) => new(input.Location, status, decoded.Platform,
                    decoded.KindLabel, decoded.World, message, outputs, decoded.GameLabel, decoded.VersionEvidence, decoded.ToyBoxVersion, decoded.CompatibilityNote);
                if (options.ToyBoxOnly && !decoded.IsToyBoxFile) {
                    result = Result("Skipped", "Not a recognized Toy Box file. Disable Toy Box files only to export it.", []);
                }
                else {
                    string packageName = input.Member == null ? new DirectoryInfo(input.Package).Name : System.IO.Path.GetFileNameWithoutExtension(input.Path);
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Package.ToUpperInvariant())))[..10];
                    string package = System.IO.Path.Combine(run, CleanSegment(packageName) + "-" + hash);
                    string relative = SafeRelativeName(input.Name);
                    void Write(string category, string suffix, byte[] bytes)
                    {
                        string file = System.IO.Path.Combine(package, category, relative + suffix);
                        string original = file;
                        int collision = 2;
                        while (!outputNames.Add(file)) file = original + "~" + collision++;
                        if (!InputScanner.IsWithin(file, run)) throw new InvalidDataException("Output path escaped the export directory.");
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                            created.Add(file); stream.Write(bytes);
                        }
                    }
                    if (options.Text) Write("text", ".txt", SaveCodec.ReadableBytes(decoded.TextBytes));
                    if (options.PcSave) Write("pc", "", SaveCodec.PcFile(decoded.Cmp1));
                    if (options.Cmp1) Write("cmp1", ".cmp1", decoded.Cmp1);
                    result = Result("Saved", "Checksums verified: compressed lookup2, text lookup2 and zlib Adler-32.",
                        created.Select(f => System.IO.Path.GetRelativePath(run, f)).ToArray());
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
                foreach (string path in created) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                result = new(input.Location, "Failed", "", "", "", e.Message, []);
            }
            results.Add(result);
            progress?.Report(new(i + 1, inputs.Count, input, result));
        }
        // Even an interrupted batch has a report of every file that finished.
        var batch = new BatchResult(run, results, cancelled);
        string json = JsonSerializer.Serialize(new { Tool = "LiteralToyBoxDecryptor 1.1.0", Created = DateTimeOffset.Now,
            Profile = options.Profile.ToString(), DI3Support = SaveCodec.DI3Notice,
            batch.Cancelled, TotalQueued = inputs.Count, batch.Saved, batch.Failed, batch.Skipped, Files = results }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(System.IO.Path.Combine(run, "report.json"), json, new UTF8Encoding(false));
        return batch;
    }

    private static string SafeRelativeName(string name)
    {
        string[] parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "save";
        return System.IO.Path.Combine(parts.Select(CleanSegment).ToArray());
    }
    private static string CleanSegment(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        string result = new(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        result = result.Trim().TrimEnd('.');
        if (result.Length > 100) result = result[..100].TrimEnd('.');
        if (result.Length == 0 || result is "." or "..") return "_";
        if (Regex.IsMatch(result, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) result = "_" + result;
        return result;
    }
}
