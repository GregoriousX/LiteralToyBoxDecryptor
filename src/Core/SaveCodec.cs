// SPDX-License-Identifier: GPL-3.0-only
// C# port of LiteralCrab src/toyboxes/save_file.cpp. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ToyBoxDecryptor.Core;

public enum GameProfile { Auto, DI1, DI3 }
public enum SaveKind { Other, SharedData, SharedHeader, SharedScreenshot, SlotData, SlotHeader, SlotScreenshot,
    ToyBoxHelp, ToyBoxMetadata, GameData, GameHeader, GameScreenshot, GameHelp, GameMetadata }

public sealed record DecodedSave(byte[] TextBytes, byte[] Cmp1, bool ConsoleEncrypted, SaveKind Kind, string World,
    GameProfile Game = GameProfile.Auto, string VersionEvidence = "", int? ToyBoxVersion = null)
{
    public string Platform => ConsoleEncrypted ? "PS3 / Wii U" : "PC";
    public string GameLabel => Game switch { GameProfile.DI1 => "DI 1.0", GameProfile.DI3 => "DI 3.0 (experimental)", _ => "Undetermined" };
    public string CompatibilityNote => Game == GameProfile.DI3 ? SaveCodec.DI3Notice : "";
    public string KindLabel => Kind switch {
        SaveKind.SharedData => "Shared Toy Box", SaveKind.SharedHeader => "Shared header",
        SaveKind.SharedScreenshot => "Shared screenshot", SaveKind.SlotData => "Toy Box save",
        SaveKind.SlotHeader => "Save header", SaveKind.SlotScreenshot => "Save screenshot",
        SaveKind.ToyBoxHelp => "Toy Box help state", SaveKind.ToyBoxMetadata => "Toy Box metadata",
        SaveKind.GameData => "Toy Box Game", SaveKind.GameHeader => "Game header", SaveKind.GameScreenshot => "Game screenshot",
        SaveKind.GameHelp => "Game help state", SaveKind.GameMetadata => "Game metadata", _ => "Other save"
    };
    public string Text => SaveCodec.DisplayText(TextBytes);
    public bool IsToyBoxFile => Kind != SaveKind.Other;
}

public static class SaveCodec
{
    public const string DI3Notice = "DI 3.0 support is experimental. PS3/Wii U decryption assumes the DI 1.0 Phelix key and variant; console samples and in-game compatibility have not been tested.";
    public const int MaxTextSize = 8 << 20;
    public const int MaxFileSize = 16 << 20;
    public static bool HasSaveHeader(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 &&
        (BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x207 || BinaryPrimitives.ReadUInt32BigEndian(bytes) == 0x207);

    public static DecodedSave Decode(byte[] file, GameProfile profile = GameProfile.Auto, string? sourceName = null)
    {
        if (file.Length < 0x54) throw new InvalidDataException("File is too short for a Disney Infinity save (minimum 84 bytes).");
        if (file.Length > MaxFileSize) throw new InvalidDataException("Save exceeds the 16 MB limit.");
        if (!HasSaveHeader(file)) throw new InvalidDataException("Not a supported Disney Infinity save. Select extracted DI 1.0 / 3.0 saves or a ZIP containing them.");
        bool console = BinaryPrimitives.ReadUInt32BigEndian(file) == 0x207;
        byte[] payload = file.AsSpan(0x40).ToArray();
        if (console) Phelix.Decrypt(payload, true);
        if (!payload.AsSpan(0, 4).SequenceEqual("CMP1"u8))
            throw new InvalidDataException("CMP1 block is missing after decryption. Unsupported or damaged save.");
        uint Read(int at) => console ? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at));
        uint textLength = Read(4), packedLength = Read(8), textHash = Read(12), packedHash = Read(16);
        if (textLength is 0 or > MaxTextSize || packedLength < 8 || packedLength > payload.Length - 20)
            throw new InvalidDataException("CMP1 sizes are invalid or the save is truncated.");
        ReadOnlySpan<byte> packed = payload.AsSpan(20, (int)packedLength);
        if (Lookup2(packed) != packedHash) throw new InvalidDataException("Compressed checksum mismatch. Save is damaged.");
        if ((packed[0] & 15) != 8 || (packed[0] >> 4) > 7 || (packed[0] * 256 + packed[1]) % 31 != 0 || (packed[1] & 32) != 0)
            throw new InvalidDataException("CMP1 does not contain a supported zlib stream.");
        byte[] text = new byte[(int)textLength];
        using (var source = new MemoryStream(payload, 20, (int)packedLength, false))
        using (var zlib = new ZLibStream(source, CompressionMode.Decompress))
        {
            try {
                zlib.ReadExactly(text);
                if (zlib.ReadByte() != -1) throw new InvalidDataException("Inflated text exceeds the declared size.");
            }
            catch (EndOfStreamException e) { throw new InvalidDataException("Inflated text is shorter than the declared size.", e); }
        }
        uint adler = BinaryPrimitives.ReadUInt32BigEndian(packed[^4..]);
        if (Adler32(text) != adler) throw new InvalidDataException("zlib Adler-32 checksum mismatch.");
        if (Lookup2(text) != textHash) throw new InvalidDataException("Text checksum mismatch. Save is damaged.");
        byte[] cmp = new byte[20 + packedLength];
        "CMP1"u8.CopyTo(cmp);
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(cmp.AsSpan(4 + i * 4), new[] { textLength, packedLength, textHash, packedHash }[i]);
        packed.CopyTo(cmp.AsSpan(20));
        string display = DisplayText(text);
        var world = Regex.Match(display, "CurrentEnv = \"([^\"]*)\"", RegexOptions.CultureInvariant);
        var identity = Identify(display, profile);
        return new(text, cmp, console, Classify(display, identity.Game, sourceName), world.Success ? world.Groups[1].Value : "",
            identity.Game, identity.Evidence, identity.RumpusVersion);
    }

    public static (GameProfile Game, string Evidence, int? RumpusVersion) Identify(string text, GameProfile selected = GameProfile.Auto)
    {
        if (!Enum.IsDefined(selected)) throw new ArgumentException("Unknown game profile.");
        int? Field(string key) {
            var match = Regex.Match(text, @"(?m)^[\t ]*" + key + @"[\t ]*=[\t ]*(\d+)[\t ]*\r?$", RegexOptions.CultureInvariant);
            return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : null;
        }
        int? infinity = Field("INFINITY_VERSION"), rr = Field("RR_SAVE_VERSION");
        GameProfile declared = infinity switch { 1 => GameProfile.DI1, 3 => GameProfile.DI3, _ => GameProfile.Auto };
        GameProfile layout = rr switch { 31 => GameProfile.DI1, 127 => GameProfile.DI3, _ => GameProfile.Auto };
        if (infinity != null && infinity is not (1 or 3)) throw new InvalidDataException($"INFINITY_VERSION = {infinity} is not supported; choose a DI 1.0 or DI 3.0 save.");
        // Older Toy Box layouts can occur in DI 3.0 collections. Version 31 is a
        // layout hint, not proof that the enclosing collection belongs to DI 1.0.
        if (declared == GameProfile.DI1 && layout == GameProfile.DI3)
            throw new InvalidDataException("Conflicting INFINITY_VERSION and RR_SAVE_VERSION markers.");
        GameProfile detected = declared != GameProfile.Auto ? declared : layout;
        bool legacyInDI3 = selected == GameProfile.DI3 && declared == GameProfile.Auto && layout == GameProfile.DI1;
        if (selected != GameProfile.Auto && detected != GameProfile.Auto && selected != detected && !legacyInDI3)
            throw new InvalidDataException($"Save content declares {(detected == GameProfile.DI1 ? "DI 1.0" : "DI 3.0")}, which conflicts with the selected profile. Use Auto or the matching profile.");
        string evidence = declared != GameProfile.Auto ? $"INFINITY_VERSION = {infinity}" : layout != GameProfile.Auto ? $"RR_SAVE_VERSION = {rr} (layout hint)" : "No distinguishing content marker; 0x207 is shared by DI 1.0 and DI 3.0.";
        if (legacyInDI3) evidence += " Legacy version-31 layout retained under the selected DI 3.0 profile.";
        if (selected != GameProfile.Auto) evidence += $" Profile selected: {(selected == GameProfile.DI3 ? "DI 3.0" : "DI 1.0")}.";
        return (selected != GameProfile.Auto ? selected : detected, evidence, rr);
    }

    // A byte-preserving export; the readable .txt export only removes terminal NUL bytes.
    public static byte[] ReadableBytes(byte[] text)
    {
        int length = text.Length;
        while (length > 0 && text[length - 1] == 0) length--;
        return text.AsSpan(0, length).ToArray();
    }
    public static string DisplayText(byte[] text)
    {
        byte[] bytes = ReadableBytes(text);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
    public static SaveKind Classify(string text, GameProfile game = GameProfile.Auto, string? sourceName = null)
    {
        bool screen = text.Contains("\r\n\tSCREENSHOT = ", StringComparison.Ordinal);
        string name = sourceName == null ? "" : Path.GetFileName(sourceName.Replace('\\', '/'));
        // Suffix letters are DI 3.0 slot variants, not file extensions. Names are hints only;
        // content roots/markers must still match. They never determine the game version.
        var family = Regex.Match(name, @"^(S|SH|SC|E|EH)(RR|RA)\d+[A-Z]*(?:\.[^.]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string prefix = family.Success ? family.Groups[1].Value.ToUpperInvariant() : "";
        bool toyBoxGame = game == GameProfile.DI3 && family.Success && family.Groups[2].Value.Equals("RA", StringComparison.OrdinalIgnoreCase);
        if (game == GameProfile.DI3 && family.Success) {
            if (prefix == "E" && text.StartsWith("HelpManager =", StringComparison.Ordinal)) return toyBoxGame ? SaveKind.GameHelp : SaveKind.ToyBoxHelp;
            if (prefix == "EH" && text.StartsWith("CSaveGameSlot =", StringComparison.Ordinal) && !screen) return toyBoxGame ? SaveKind.GameMetadata : SaveKind.ToyBoxMetadata;
            if (toyBoxGame && text.StartsWith("CSaveGameSlot =", StringComparison.Ordinal)) return screen ? SaveKind.GameScreenshot : SaveKind.GameHeader;
        }
        if (text.StartsWith("CSaveGameUGC =", StringComparison.Ordinal)) return screen ? SaveKind.SharedScreenshot : SaveKind.SharedHeader;
        if (text.StartsWith("CSaveGameSlot =", StringComparison.Ordinal)) return screen ? SaveKind.SlotScreenshot : SaveKind.SlotHeader;
        bool box = text.Contains("RumpusSaveGame =\r\n", StringComparison.Ordinal);
        if (box && game == GameProfile.DI3 && (text.StartsWith("zone =", StringComparison.Ordinal) || text.StartsWith("zoneglobal =", StringComparison.Ordinal))) {
            if (toyBoxGame) return SaveKind.GameData;
            if (family.Success && prefix == "S") return SaveKind.SlotData;
            // Both root layouts occur in DI 3.0; without a filename hint, avoid claiming UGC.
            return SaveKind.SlotData;
        }
        if (box && text.StartsWith("zone =", StringComparison.Ordinal)) return SaveKind.SharedData;
        if (box && text.StartsWith("zoneglobal =", StringComparison.Ordinal)) return SaveKind.SlotData;
        return SaveKind.Other;
    }
    public static byte[] PcFile(byte[] cmp1)
    {
        if (cmp1.Length < 20 || !cmp1.AsSpan(0, 4).SequenceEqual("CMP1"u8)) throw new InvalidDataException("Invalid normalized CMP1 block.");
        int padded = (cmp1.Length + 63) & ~63;
        byte[] file = new byte[64 + padded];
        uint[] header = [0x207, (uint)file.Length, 1, BinaryPrimitives.ReadUInt32LittleEndian(cmp1.AsSpan(4)), (uint)padded];
        for (int i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(i * 4), header[i]);
        cmp1.CopyTo(file, 64);
        return file;
    }
    public static byte[] EncodeCmp1(byte[] text)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, true)) zlib.Write(text);
        byte[] packed = buffer.ToArray(), cmp = new byte[20 + packed.Length];
        "CMP1"u8.CopyTo(cmp);
        uint[] fields = [(uint)text.Length, (uint)packed.Length, Lookup2(text), Lookup2(packed)];
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(cmp.AsSpan(4 + i * 4), fields[i]);
        packed.CopyTo(cmp, 20);
        return cmp;
    }
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return b << 16 | a;
    }
    public static uint Lookup2(ReadOnlySpan<byte> data, uint initial = 0)
    {
        unchecked {
            uint a = 0x9e3779b9, b = a, c = initial;
            int pos = 0;
            while (data.Length - pos >= 12) {
                a += BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                b += BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
                c += BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 8)..]);
                Mix(ref a, ref b, ref c); pos += 12;
            }
            c += (uint)data.Length;
            var tail = data[pos..];
            if (tail.Length >= 11) c += (uint)tail[10] << 24;
            if (tail.Length >= 10) c += (uint)tail[9] << 16;
            if (tail.Length >= 9) c += (uint)tail[8] << 8;
            if (tail.Length >= 8) b += (uint)tail[7] << 24;
            if (tail.Length >= 7) b += (uint)tail[6] << 16;
            if (tail.Length >= 6) b += (uint)tail[5] << 8;
            if (tail.Length >= 5) b += tail[4];
            if (tail.Length >= 4) a += (uint)tail[3] << 24;
            if (tail.Length >= 3) a += (uint)tail[2] << 16;
            if (tail.Length >= 2) a += (uint)tail[1] << 8;
            if (tail.Length >= 1) a += tail[0];
            Mix(ref a, ref b, ref c); return c;
        }
    }
    private static void Mix(ref uint a, ref uint b, ref uint c)
    {
        unchecked {
            a -= b; a -= c; a ^= c >> 13; b -= c; b -= a; b ^= a << 8; c -= a; c -= b; c ^= b >> 13;
            a -= b; a -= c; a ^= c >> 12; b -= c; b -= a; b ^= a << 16; c -= a; c -= b; c ^= b >> 5;
            a -= b; a -= c; a ^= c >> 3; b -= c; b -= a; b ^= a << 10; c -= a; c -= b; c ^= b >> 15;
        }
    }
}

public sealed class Phelix
{
    private readonly uint[] z = new uint[5], x0 = new uint[8], x1 = new uint[8];
    private uint block;
    private static uint Rotate(uint value, int count) => (value << count) | (value >> (32 - count));
    public Phelix(bool bigEndian)
    {
        unchecked {
            byte[] key = new byte[32];
            byte[] literal = Encoding.ASCII.GetBytes("IT aL1 stArt3d w1TH a m0uSe.:-)");
            literal.CopyTo(key, 0);
            uint length = (uint)literal.Length;
            uint[] x = new uint[8];
            for (int i = 0; i < 8; i++) x[i] = ReadWord(key.AsSpan(i * 4), bigEndian);
            for (int round = 0; round < 8; round++) {
                int k = 4 * (round & 1);
                uint[] mixed = [x[k], x[k + 1], x[k + 2], x[k + 3], length + 64];
                Step(mixed, 0, 0, 0);
                for (int i = 0; i < 4; i++) x[(k ^ 4) + i] ^= mixed[i];
            }
            x.CopyTo(x0, 0);
            for (uint i = 0; i < 4; i++) { x1[i] = x[i + 4]; x1[i + 4] = x[i] + i; }
            x1[1] += length * 4; x1[5] += length * 4;
            for (int i = 0; i < 5; i++) z[i] = x[i + 3];
            for (int round = 0; round < 8; round++) Next(0);
        }
    }
    public static uint ReadWord(ReadOnlySpan<byte> bytes, bool bigEndian) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    public static void WriteWord(Span<byte> bytes, uint value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    }
    private static void Step(uint[] state, uint key0, uint text, uint key1)
    {
        unchecked {
            uint z0 = state[0], z1 = state[1], z2 = state[2], z3 = state[3], z4 = state[4];
            z1 += z4; z0 += z3; z2 ^= z0; z3 = Rotate(z3, 15) ^ z1;
            z4 = Rotate(z4, 25) + z2; z0 = Rotate(z0, 9) ^ (key0 + z3);
            z1 = Rotate(z1, 10) ^ z4; z2 = Rotate(z2, 17) + z0; z3 = Rotate(z3, 30) + z1;
            z4 = Rotate(z4, 13) ^ z2; z0 = Rotate(z0, 20) + (z3 ^ text);
            z1 = Rotate(z1, 11) + z4; z2 = Rotate(z2, 5) ^ z0; z3 = Rotate(z3, 15) ^ z1;
            z4 = Rotate(z4, 25) + z2; z0 = Rotate(z0, 9) ^ (key1 + z3);
            z1 = Rotate(z1, 10) ^ z4; z2 = Rotate(z2, 17) + z0;
            state[0] = Rotate(z0, 20); state[1] = Rotate(z1, 11); state[2] = Rotate(z2, 5);
            state[3] = Rotate(z3, 30) + z1; state[4] = Rotate(z4, 13) ^ z2;
        }
    }
    private void Next(uint text)
    {
        unchecked { int k = (int)(block & 7); Step(z, x0[k], text, x1[k] + block); block++; }
    }
    public static void Decrypt(Span<byte> data, bool bigEndian)
    {
        var cipher = new Phelix(bigEndian);
        for (int offset = 0; offset + 4 <= data.Length; offset += 4) {
            uint text = ReadWord(data[offset..], bigEndian) ^ cipher.z[0];
            WriteWord(data[offset..], text, bigEndian); cipher.Next(text);
        }
    }
}
