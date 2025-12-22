using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SteamRoll.Services;

namespace SteamRoll.Parsers;

/// <summary>
/// Lightweight PE (Portable Executable) parser for DRM detection.
/// Reads headers, imports, sections, and exports without loading the PE.
/// </summary>
public class PeParser
{
    public bool IsValid { get; private set; }
    public bool Is64Bit { get; private set; }
    public List<string> ImportedDlls { get; private set; } = new();
    public List<string> ImportedFunctions { get; private set; } = new();
    public List<PeSection> Sections { get; private set; } = new();
    public long OverlaySize { get; private set; }
    public long FileSize { get; private set; }
    public string? FilePath { get; private set; }

    // PE Constants
    private const ushort DOS_SIGNATURE = 0x5A4D;        // "MZ"
    private const uint PE_SIGNATURE = 0x00004550;       // "PE\0\0"
    private const ushort PE32_MAGIC = 0x10B;
    private const ushort PE32PLUS_MAGIC = 0x20B;

    /// <summary>
    /// Parses a PE file and extracts relevant information.
    /// </summary>
    public static PeParser? Parse(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var parser = new PeParser { FilePath = filePath };
            parser.ParseFile(filePath);
            return parser.IsValid ? parser : null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"PE parse error for {filePath}: {ex.Message}", "PeParser");
            return null;
        }
    }

    private void ParseFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs);

        FileSize = fs.Length;

        // Read DOS header
        if (fs.Length < 64) return;
        var dosSignature = reader.ReadUInt16();
        if (dosSignature != DOS_SIGNATURE) return;

        // Get PE header offset
        fs.Seek(0x3C, SeekOrigin.Begin);
        var peOffset = reader.ReadUInt32();
        if (peOffset >= fs.Length - 4) return;

        // Read PE signature
        fs.Seek(peOffset, SeekOrigin.Begin);
        var peSignature = reader.ReadUInt32();
        if (peSignature != PE_SIGNATURE) return;

        IsValid = true;

        // Read COFF header (20 bytes)
        var machine = reader.ReadUInt16();
        var numberOfSections = reader.ReadUInt16();
        reader.ReadUInt32(); // TimeDateStamp
        reader.ReadUInt32(); // PointerToSymbolTable
        reader.ReadUInt32(); // NumberOfSymbols
        var sizeOfOptionalHeader = reader.ReadUInt16();
        reader.ReadUInt16(); // Characteristics

        // Read Optional header magic
        var optionalHeaderStart = fs.Position;
        var magic = reader.ReadUInt16();
        Is64Bit = magic == PE32PLUS_MAGIC;

        // Parse sections
        var sectionTableOffset = optionalHeaderStart + sizeOfOptionalHeader;
        ParseSections(reader, sectionTableOffset, numberOfSections);

        // Calculate overlay size (data appended after PE)
        CalculateOverlay();

        // Parse import table
        ParseImports(reader, optionalHeaderStart, magic);
    }

    private void ParseSections(BinaryReader reader, long sectionTableOffset, ushort count)
    {
        reader.BaseStream.Seek(sectionTableOffset, SeekOrigin.Begin);

        for (int i = 0; i < count; i++)
        {
            var nameBytes = reader.ReadBytes(8);
            var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            
            var virtualSize = reader.ReadUInt32();
            var virtualAddress = reader.ReadUInt32();
            var sizeOfRawData = reader.ReadUInt32();
            var pointerToRawData = reader.ReadUInt32();
            reader.ReadBytes(12); // Skip rest of section header
            var characteristics = reader.ReadUInt32();

            Sections.Add(new PeSection
            {
                Name = name,
                VirtualSize = virtualSize,
                VirtualAddress = virtualAddress,
                RawDataSize = sizeOfRawData,
                RawDataPointer = pointerToRawData,
                Characteristics = characteristics
            });
        }
    }

    private void CalculateOverlay()
    {
        if (Sections.Count == 0) return;

        // Find the end of the last section
        long lastSectionEnd = 0;
        foreach (var section in Sections)
        {
            var sectionEnd = section.RawDataPointer + section.RawDataSize;
            if (sectionEnd > lastSectionEnd)
                lastSectionEnd = sectionEnd;
        }

        OverlaySize = FileSize - lastSectionEnd;
        if (OverlaySize < 0) OverlaySize = 0;
    }

    private void ParseImports(BinaryReader reader, long optionalHeaderStart, ushort magic)
    {
        try
        {
            // Navigate to import directory RVA
            // PE32: offset 104 from optional header start
            // PE64: offset 120 from optional header start
            var importDirOffset = Is64Bit ? 120 : 104;
            reader.BaseStream.Seek(optionalHeaderStart + importDirOffset, SeekOrigin.Begin);

            var importRva = reader.ReadUInt32();
            var importSize = reader.ReadUInt32();

            if (importRva == 0 || importSize == 0) return;

            // Convert RVA to file offset
            var importOffset = RvaToOffset(importRva);
            if (importOffset == 0) return;

            reader.BaseStream.Seek(importOffset, SeekOrigin.Begin);

            // Read import descriptors (20 bytes each)
            while (true)
            {
                var originalFirstThunk = reader.ReadUInt32();
                reader.ReadUInt32(); // TimeDateStamp
                reader.ReadUInt32(); // ForwarderChain
                var nameRva = reader.ReadUInt32();
                var firstThunk = reader.ReadUInt32();

                // End of import descriptors
                if (nameRva == 0) break;

                // Read DLL name
                var nameOffset = RvaToOffset(nameRva);
                if (nameOffset > 0)
                {
                    var currentPos = reader.BaseStream.Position;
                    reader.BaseStream.Seek(nameOffset, SeekOrigin.Begin);
                    var dllName = ReadNullTerminatedString(reader);
                    ImportedDlls.Add(dllName.ToLowerInvariant());
                    reader.BaseStream.Seek(currentPos, SeekOrigin.Begin);

                    // Read imported function names
                    if (originalFirstThunk != 0)
                    {
                        ParseImportedFunctions(reader, originalFirstThunk);
                    }
                }
            }
        }
        catch
        {
            // Import parsing is best-effort
        }
    }

    private void ParseImportedFunctions(BinaryReader reader, uint thunkRva)
    {
        var thunkOffset = RvaToOffset(thunkRva);
        if (thunkOffset == 0) return;

        var currentPos = reader.BaseStream.Position;
        reader.BaseStream.Seek(thunkOffset, SeekOrigin.Begin);

        try
        {
            // Read up to 50 functions per DLL to avoid excessive parsing
            int count = 0;
            while (count < 50)
            {
                ulong thunkData = Is64Bit ? reader.ReadUInt64() : reader.ReadUInt32();
                if (thunkData == 0) break;

                // Check if import by ordinal (high bit set)
                var ordinalFlag = Is64Bit ? 0x8000000000000000UL : 0x80000000UL;
                if ((thunkData & ordinalFlag) != 0)
                {
                    count++;
                    continue; // Skip ordinal imports
                }

                // Read function name
                var hintNameRva = (uint)(thunkData & 0x7FFFFFFF);
                var hintNameOffset = RvaToOffset(hintNameRva);
                if (hintNameOffset > 0)
                {
                    var pos = reader.BaseStream.Position;
                    reader.BaseStream.Seek(hintNameOffset + 2, SeekOrigin.Begin); // Skip hint
                    var funcName = ReadNullTerminatedString(reader);
                    if (!string.IsNullOrEmpty(funcName))
                        ImportedFunctions.Add(funcName);
                    reader.BaseStream.Seek(pos, SeekOrigin.Begin);
                }
                count++;
            }
        }
        finally
        {
            reader.BaseStream.Seek(currentPos, SeekOrigin.Begin);
        }
    }

    private long RvaToOffset(uint rva)
    {
        foreach (var section in Sections)
        {
            if (rva >= section.VirtualAddress && 
                rva < section.VirtualAddress + section.VirtualSize)
            {
                return rva - section.VirtualAddress + section.RawDataPointer;
            }
        }
        return 0;
    }

    private static string ReadNullTerminatedString(BinaryReader reader, int maxLength = 256)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < maxLength; i++)
        {
            var c = reader.ReadByte();
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Searches the file for specific byte patterns/strings using buffered reading.
    /// </summary>
    public bool ContainsString(string searchString, bool caseInsensitive = true)
    {
        if (FilePath == null || string.IsNullOrEmpty(searchString)) return false;

        try
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var searchBytes = Encoding.ASCII.GetBytes(searchString);
            var searchBytesLower = caseInsensitive ? 
                Encoding.ASCII.GetBytes(searchString.ToLowerInvariant()) : null;

            // Buffer size: 64KB
            const int bufferSize = 64 * 1024;
            var buffer = new byte[bufferSize];
            // We need to keep the overlap of searchString.Length - 1 to ensure we don't miss a string crossing buffer boundary
            int overlap = searchBytes.Length - 1;
            int bytesRead;
            int offset = 0;

            while ((bytesRead = fs.Read(buffer, offset, buffer.Length - offset)) > 0)
            {
                int validBytes = offset + bytesRead;

                // Scan the buffer
                for (int i = 0; i <= validBytes - searchBytes.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < searchBytes.Length; j++)
                    {
                        var b = buffer[i + j];
                        if (caseInsensitive)
                        {
                            if (b >= 'A' && b <= 'Z') b = (byte)(b + 32);
                        }

                        var target = caseInsensitive ? searchBytesLower![j] : searchBytes[j];
                        if (b != target)
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) return true;
                }

                // Prepare for next chunk
                if (validBytes < buffer.Length) break; // End of file

                // Move the overlap to the beginning of the buffer
                if (overlap > 0)
                {
                    Array.Copy(buffer, buffer.Length - overlap, buffer, 0, overlap);
                }
                offset = overlap;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"PeParser.ContainsString error: {ex.Message}", "PeParser");
        }

        return false;
    }

    /// <summary>
    /// Checks if specific DLL is imported.
    /// </summary>
    public bool ImportsDll(string dllName)
    {
        return ImportedDlls.Any(d => 
            d.Equals(dllName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if specific function is imported.
    /// </summary>
    public bool ImportsFunction(string functionName)
    {
        return ImportedFunctions.Any(f => 
            f.Equals(functionName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the architecture string (32-bit or 64-bit).
    /// </summary>
    public string GetArchitecture()
    {
        return Is64Bit ? "64-bit" : "32-bit";
    }
}

/// <summary>
/// Represents a PE section.
/// </summary>
public class PeSection
{
    public string Name { get; set; } = "";
    public uint VirtualSize { get; set; }
    public uint VirtualAddress { get; set; }
    public uint RawDataSize { get; set; }
    public uint RawDataPointer { get; set; }
    public uint Characteristics { get; set; }

    /// <summary>
    /// Calculates the entropy of this section (high entropy = packed/encrypted).
    /// </summary>
    public double CalculateEntropy(BinaryReader reader)
    {
        if (RawDataSize == 0) return 0;

        try
        {
            reader.BaseStream.Seek(RawDataPointer, SeekOrigin.Begin);
            var data = reader.ReadBytes((int)Math.Min(RawDataSize, 65536)); // Sample first 64KB
            
            var frequency = new int[256];
            foreach (var b in data)
                frequency[b]++;

            double entropy = 0;
            foreach (var count in frequency)
            {
                if (count == 0) continue;
                double p = (double)count / data.Length;
                entropy -= p * Math.Log2(p);
            }
            return entropy;
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"PeParser.CalculateEntropy error: {ex.Message}", "PeParser");
            return 0;
        }
    }
}

