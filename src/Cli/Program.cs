// SPDX-License-Identifier: GPL-3.0-only
using ToyBoxDecryptor.Core;

if (args.Length == 0 || args.Contains("--help")) {
    Console.WriteLine("""
    LiteralToyBoxDecryptor — DI 1.0 / DI 3.0 (experimental)
    Usage: LiteralToyBoxBatch --output <folder> [options] <files, folders or ZIPs...>
      --formats text,pc,cmp1   Default: text,pc
      --game auto|1|3         Game profile; default auto. Select 3 for DI 3.0 companions without version markers.
      --all-saves             Include other save types
      --verify-only           Check inputs without exporting anything
      --help                  Show this help
    Source files are read only. Each export uses a fresh folder and report.json.
    DI 3.0 console decryption and in-game compatibility have not been tested.
    Exit codes: 0 = success, 1 = failed files/scan issues, 2 = usage/fatal error, 3 = cancelled.
    """);
    return 0;
}
try {
    string output = Path.Combine(AppContext.BaseDirectory, "Exports");
    string formats = "text,pc"; bool all = false, verify = false;
    GameProfile profile = GameProfile.Auto;
    var inputs = new List<string>();
    for (int i = 0; i < args.Length; i++) {
        switch (args[i]) {
            case "--output": if (++i >= args.Length) throw new ArgumentException("--output needs a folder."); output = args[i]; break;
            case "--formats": if (++i >= args.Length) throw new ArgumentException("--formats needs a list."); formats = args[i]; break;
            case "--game":
                if (++i >= args.Length) throw new ArgumentException("--game needs auto, 1 or 3.");
                profile = args[i].ToLowerInvariant() switch { "auto" => GameProfile.Auto, "1" or "1.0" => GameProfile.DI1,
                    "3" or "3.0" => GameProfile.DI3, _ => throw new ArgumentException("--game must be auto, 1 or 3.") };
                break;
            case "--all-saves": all = true; break;
            case "--verify-only": verify = true; break;
            default: if (args[i].StartsWith("--")) throw new ArgumentException("Unknown option: " + args[i]); inputs.Add(args[i]); break;
        }
    }
    if (inputs.Count == 0) throw new ArgumentException("Supply at least one input path.");
    string[] chosen = formats.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (chosen.Length == 0 || chosen.Any(f => f is not ("text" or "pc" or "cmp1"))) throw new ArgumentException("Formats must be text, pc and/or cmp1.");
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var scan = InputScanner.Scan(inputs, cancellation.Token, output);
    foreach (string message in scan.Messages) Console.Error.WriteLine("SCAN: " + message);
    Console.WriteLine($"Found {scan.Files.Count} saves; ignored {scan.Ignored} unrelated files.");
    if (scan.Files.Count == 0) { Console.Error.WriteLine("No extracted DI 1.0 / 3.0 saves were found."); return 1; }
    Console.WriteLine(SaveCodec.DI3Notice);
    if (verify) {
        int errors = 0, checkedFiles = 0;
        var kinds = new Dictionary<string, int>();
        foreach (var input in scan.Files) {
            if (cancellation.IsCancellationRequested) return 3;
            try {
                var save = SaveCodec.Decode(input.Read(), profile, input.Name); checkedFiles++;
                string kind = save.GameLabel + " / " + save.Platform + " / " + save.KindLabel;
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { errors++; Console.Error.WriteLine(input.Location + ": " + e.Message); }
        }
        Console.WriteLine($"Verified {checkedFiles}; failed {errors}.");
        foreach (var kind in kinds.OrderBy(k => k.Key)) Console.WriteLine($"  {kind.Key}: {kind.Value}");
        return errors > 0 || scan.Messages.Count > 0 ? 1 : 0;
    }
    var result = BatchExporter.Run(scan.Files, output, new(chosen.Contains("text"), chosen.Contains("pc"), chosen.Contains("cmp1"), !all, profile), null, cancellation.Token);
    foreach (var file in result.Files.Where(f => f.Status == "Failed")) Console.Error.WriteLine(file.Source + ": " + file.Message);
    Console.WriteLine($"Saved {result.Saved}; skipped {result.Skipped}; failed {result.Failed}.");
    Console.WriteLine("Output: " + result.Directory);
    return result.Cancelled ? 3 : result.Failed > 0 || scan.Messages.Count > 0 ? 1 : 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 3; }
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { Console.Error.WriteLine(e.Message); return 2; }
