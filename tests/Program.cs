// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ToyBoxDecryptor.Core;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("FAIL (accepted invalid input): " + name); }
byte[] sample = Encoding.UTF8.GetBytes("zone =\r\n{\r\n\tRumpusSaveGame =\r\n\t{\r\n\t\tCurrentEnv = \"rumpusroom_blank\"\r\n\t}\r\n}\r\n\0");

foreach (var pair in new[] { ("", 0xbd49d10du), ("a", 0x29eec818u), ("Disney Infinity", 0xfe3e2d2cu), ("CMP1 toy box hash test 0123456789", 0x2fef568cu) })
    Check(SaveCodec.Lookup2(Encoding.ASCII.GetBytes(pair.Item1)) == pair.Item2, "LiteralCrab lookup2 vector: " + pair.Item1);
foreach (var vector in new[] {
    (false, "ea69bf8d3e1f92fe1e627908f014fe32", "434d5031000074ef0000aaaa00000000"),
    (true, "4f5dd23eb79fb40fcb8fbd1116c2a4603d03b54292975124462c2f6392dd80a8", "434d5031000074ef000020a8a0ca936b5fa5d7957801ed9d5baf1cc771c7f37a") }) {
    byte[] bytes = Convert.FromHexString(vector.Item2); Phelix.Decrypt(bytes, vector.Item1);
    Check(bytes.SequenceEqual(Convert.FromHexString(vector.Item3)), "LiteralCrab Phelix vector " + vector.Item1);
}
byte[] partial = Convert.FromHexString("ea69bf8d3e1f92fe1e627908f014fe32aabb"); Phelix.Decrypt(partial, false);
Check(partial[^2] == 0xaa && partial[^1] == 0xbb, "Phelix leaves incomplete word unchanged");
foreach (byte[] bytes in new[] { sample, Encoding.UTF8.GetBytes(new string('x', 200000)), Enumerable.Range(0, 100000).Select(i => (byte)(i % 256)).ToArray() }) {
    byte[] pc = SaveCodec.PcFile(SaveCodec.EncodeCmp1(bytes)); var decoded = SaveCodec.Decode(pc);
    Check(decoded.TextBytes.SequenceEqual(bytes), "PC bytes round trip " + bytes.Length);
    Check(pc.Length % 64 == 0, "PC 64-byte padding");
}
byte[] valid = SaveCodec.PcFile(SaveCodec.EncodeCmp1(sample));
var toyBox = SaveCodec.Decode(valid);
Check(toyBox.Kind == SaveKind.SharedData && toyBox.World == "rumpusroom_blank", "content recognition");
Check(SaveCodec.ReadableBytes(sample).Length == sample.Length - 1, "only terminal NUL removed in readable export");
Check(SaveCodec.DisplayText([0xe9]) == "é", "legacy text preview fallback");
Reject(() => SaveCodec.Decode([1, 2, 3]), "short file");
Reject(() => SaveCodec.Decode(new byte[100]), "unknown version");
var damaged = (byte[])valid.Clone(); damaged[90] ^= 1;
Reject(() => SaveCodec.Decode(damaged), "compressed lookup2 mismatch");
damaged = (byte[])valid.Clone(); damaged[76] ^= 1;
Reject(() => SaveCodec.Decode(damaged), "text lookup2 mismatch");
damaged = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(68), SaveCodec.MaxTextSize + 1);
Reject(() => SaveCodec.Decode(damaged), "oversized decoded data");
Reject(() => SaveCodec.Decode(valid[..85]), "truncated CMP1 stream");

byte[] di3Text = Encoding.UTF8.GetBytes("zone =\r\n{\r\n\tRumpusSaveGame =\r\n\t{\r\n\t\tCurrentEnv = \"rumpusroom_newterrain\"\r\n\t\tRR_SAVE_VERSION = 127\r\n\t}\r\n}\r\n\0");
byte[] di3 = SaveCodec.PcFile(SaveCodec.EncodeCmp1(di3Text));
var di3Decoded = SaveCodec.Decode(di3, GameProfile.Auto, "folder/SRR260E");
Check(di3Decoded.Game == GameProfile.DI3 && di3Decoded.ToyBoxVersion == 127, "DI3 detected from RR_SAVE_VERSION 127");
Check(di3Decoded.Kind == SaveKind.SlotData && di3Decoded.World == "rumpusroom_newterrain", "DI3 zone-only local data recognized");
Check(di3Decoded.GameLabel.Contains("experimental") && di3Decoded.CompatibilityNote.Contains("have not been tested"), "DI3 remains visibly untested");
Check(di3Decoded.TextBytes.SequenceEqual(di3Text), "DI3 bytes retained without restructuring");
var di1Text = Encoding.UTF8.GetBytes("zone =\r\n{\r\n\tRumpusSaveGame =\r\n\t{\r\n\t\tRR_SAVE_VERSION = 31\r\n\t}\r\n}\r\n\0");
var di1 = SaveCodec.Decode(SaveCodec.PcFile(SaveCodec.EncodeCmp1(di1Text)));
Check(di1.Game == GameProfile.DI1 && di1.Kind == SaveKind.SharedData && di1.CompatibilityNote.Length == 0, "DI1 content recognition unchanged");
Check(SaveCodec.Decode(valid).Game == GameProfile.Auto, "shared 0x207 header does not identify game version");
Check(SaveCodec.Decode(valid, GameProfile.DI3).Game == GameProfile.DI3, "explicit DI3 profile for unmarked saves");
Reject(() => SaveCodec.Decode(di3, GameProfile.DI1), "wrong selected profile rejected");
Reject(() => SaveCodec.Identify("INFINITY_VERSION = 2\r\n"), "DI2 explicit marker unsupported");
Reject(() => SaveCodec.Identify("INFINITY_VERSION = 1\r\nRR_SAVE_VERSION = 127\r\n"), "conflicting content markers rejected");
Check(SaveCodec.Identify("\tINFINITY_VERSION = 3\r\n").Game == GameProfile.DI3, "explicit infinity metadata marker");
Check(SaveCodec.Identify("RR_SAVE_VERSION = 99\r\n").Game == GameProfile.Auto, "unknown RR version not guessed as DI3");
Check(SaveCodec.Identify("RR_SAVE_VERSION = 31\r\n", GameProfile.DI3).Game == GameProfile.DI3, "legacy Toy Box layout allowed in DI3 collection");
Check(SaveCodec.Identify("INFINITY_VERSION = 3\r\nRR_SAVE_VERSION = 31\r\n").Game == GameProfile.DI3, "DI3 marker takes precedence over legacy layout hint");
Check(SaveCodec.Classify("HelpManager =\r\n{\r\n}\r\n", GameProfile.DI3, "ERR118") == SaveKind.ToyBoxHelp, "DI3 ERR companion recognized");
Check(SaveCodec.Classify("HelpManager =\r\n{\r\n}\r\n", GameProfile.DI3, "ERA10") == SaveKind.GameHelp, "DI3 ERA game companion recognized");
Check(SaveCodec.Classify("HelpManager =\r\n{\r\n}\r\n", GameProfile.DI3, "EBA0") == SaveKind.Other, "unrelated help state excluded");
Check(SaveCodec.Classify("HelpManager =\r\n{\r\n}\r\n", GameProfile.DI1, "ERR118") == SaveKind.Other, "DI1 help classification unchanged");
Check(SaveCodec.Classify("not a save", GameProfile.DI3, "ERR118") == SaveKind.Other, "filename alone cannot classify unrelated text");
Check(SaveCodec.Classify("CSaveGameSlot =\r\n{}\r\n", GameProfile.DI3, "EHRR178") == SaveKind.ToyBoxMetadata, "DI3 metadata header recognized");
Check(SaveCodec.Classify("CSaveGameSlot =\r\n{}\r\n", GameProfile.DI3, "EHRA75") == SaveKind.GameMetadata, "DI3 game metadata recognized");
Check(SaveCodec.Classify("CSaveGameSlot =\r\n{}\r\n", GameProfile.DI3, "SHRA87A") == SaveKind.GameHeader, "DI3 letter-suffixed game header");
Check(SaveCodec.Classify("CSaveGameSlot =\r\n\tSCREENSHOT = \r\n", GameProfile.DI3, "SCRA28A") == SaveKind.GameScreenshot, "DI3 game screenshot");
Check(SaveCodec.Decode(di3, GameProfile.DI3, "SRA10A").Kind == SaveKind.GameData, "DI3 game data");

string root = Path.GetFullPath(args.Length > 0 ? args[0] : "test-output"); Directory.CreateDirectory(root);
File.WriteAllBytes(Path.Combine(root, "U1"), valid);
File.WriteAllBytes(Path.Combine(root, "broken"), new byte[100]);
File.WriteAllBytes(Path.Combine(root, "profile"), SaveCodec.PcFile(SaveCodec.EncodeCmp1("HelpManager =\r\n{\r\n}\r\n\0"u8.ToArray())));
File.WriteAllBytes(Path.Combine(root, "SRR260E"), di3);
File.WriteAllBytes(Path.Combine(root, "ERR260"), SaveCodec.PcFile(SaveCodec.EncodeCmp1("HelpManager =\r\n{\r\n}\r\n\0"u8.ToArray())));
string zipPath = Path.Combine(root, "package.zip");
using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) {
    foreach (string name in new[] { "folder/U1", "../../escape/U1", "C:/absolute/U1", "folder/CON", "folder/a?b", "folder/a*b" }) {
        using var member = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); member.Write(valid);
    }
    using var readme = zip.CreateEntry("README.txt").Open(); readme.Write("This is not a save."u8);
}
string exports = Path.Combine(root, "exports");
var scan = InputScanner.Scan([zipPath, Path.Combine(root, "U1"), Path.Combine(root, "U1")], CancellationToken.None);
Check(scan.Files.Count == 7 && scan.Ignored == 1, "ZIP + loose scan, duplicate input suppressed");
var batch = BatchExporter.Run(scan.Files, exports, new(true, true, true), null, CancellationToken.None);
Check(batch.Saved == 7 && batch.Failed == 0, "batch exports including unusual ZIP names");
foreach (var file in batch.Files) foreach (string output in file.Outputs) Check(InputScanner.IsWithin(Path.Combine(batch.Directory, output), batch.Directory), "ZIP path containment");
foreach (string file in Directory.EnumerateFiles(batch.Directory, "*", SearchOption.AllDirectories).Where(f => f.Contains(Path.DirectorySeparatorChar + "pc" + Path.DirectorySeparatorChar)))
    Check(SaveCodec.Decode(File.ReadAllBytes(file)).TextBytes.SequenceEqual(sample), "exported PC save can be decoded again");
var mixed = BatchExporter.Run([new(Path.Combine(root, "broken")), new(Path.Combine(root, "profile")), new(Path.Combine(root, "U1"))], exports, new(), null, CancellationToken.None);
Check(mixed.Failed == 1 && mixed.Skipped == 1 && mixed.Saved == 1, "failed/skipped files do not stop batch");
Check(File.ReadAllBytes(Path.Combine(root, "U1")).SequenceEqual(valid), "source unchanged");
Check(Directory.Exists(batch.Directory) && mixed.Directory != batch.Directory, "fresh output per run");
using var stop = new CancellationTokenSource(); stop.Cancel();
var cancelled = BatchExporter.Run([new(Path.Combine(root, "U1"))], exports, new(), null, stop.Token);
Check(cancelled.Cancelled && cancelled.Saved == 0 && File.Exists(Path.Combine(cancelled.Directory, "report.json")), "cancelled run report");
Check(!Directory.Exists(Path.Combine(root, "escape")), "ZIP never extracted beside package");
var experimental = BatchExporter.Run([new(Path.Combine(root, "SRR260E")), new(Path.Combine(root, "ERR260"))], exports, new(true, true, true, true, GameProfile.DI3), null, CancellationToken.None);
Check(experimental.Saved == 2 && experimental.Skipped == 0, "DI3 data and help companion export in ToyBoxOnly mode");
Check(experimental.Files.All(f => f.Game.Contains("experimental") && f.Compatibility.Contains("have not been tested")), "DI3 report entries carry experimental status");
Check(experimental.Files[0].ToyBoxVersion == 127 && experimental.Files[1].VersionEvidence.Contains("Profile selected: DI 3.0"), "report separates detected and selected identity");
Check(File.ReadAllText(Path.Combine(experimental.Directory, "report.json")).Contains("DI3Support"), "JSON includes DI3 compatibility notice");
foreach (var entry in experimental.Files) {
    string pc = entry.Outputs.Single(f => f.Contains(Path.DirectorySeparatorChar + "pc" + Path.DirectorySeparatorChar));
    var roundTrip = SaveCodec.Decode(File.ReadAllBytes(Path.Combine(experimental.Directory, pc)), GameProfile.DI3, Path.GetFileName(pc));
    Check(roundTrip.TextBytes.SequenceEqual(entry.Source.EndsWith("SRR260E") ? di3Text : "HelpManager =\r\n{\r\n}\r\n\0"u8.ToArray()), "DI3 exported text remains exact");
}
Console.WriteLine($"PASS: {checks} checks. Test artifacts: {root}");
