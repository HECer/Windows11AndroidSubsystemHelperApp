using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WsaHelper.Core;

public static class ApkManifest
{
    public static string PackageFromFile(string path)
    {
        using var apk = ZipFile.OpenRead(Safety.Apk(path));
        var entry = apk.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("The APK has no AndroidManifest.xml.");
        if (entry.Length is < 8 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid APK manifest size.");
        using var source = entry.Open(); using var memory = new MemoryStream(); source.CopyTo(memory);
        return ReadPackage(memory.ToArray());
    }
    public static string ReadPackage(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("Truncated manifest.");
        if (data[0] == '<')
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            return Safety.Package(XDocument.Load(reader).Root?.Attribute("package")?.Value ?? "");
        }
        int U16(int offset) => offset >= 0 && offset <= data.Length - 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)) : throw new InvalidDataException("Truncated binary manifest.");
        int I32(int offset) => offset >= 0 && offset <= data.Length - 4 ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) : throw new InvalidDataException("Truncated binary manifest.");
        if (U16(0) != 3 || U16(2) != 8 || I32(4) != data.Length) throw new InvalidDataException("Invalid binary manifest header.");
        string[] strings = [];
        for (int offset = 8; offset <= data.Length - 8;)
        {
            var type = U16(offset); var header = U16(offset + 2); var size = I32(offset + 4);
            if (header < 8 || size < header || size > data.Length - offset) throw new InvalidDataException("Invalid manifest chunk.");
            var end = offset + size;
            if (type == 1)
            {
                if (header < 28) throw new InvalidDataException("Invalid string pool.");
                var count = I32(offset + 8); var flags = I32(offset + 16); var start = I32(offset + 20);
                if (count < 0 || count > 100000 || count > (size - header) / 4 || start < header || start > size) throw new InvalidDataException("Invalid string count.");
                strings = new string[count];
                for (var i = 0; i < count; i++)
                {
                    var position = (long)offset + start + I32(offset + header + i * 4);
                    if (position < offset + start || position >= end) throw new InvalidDataException("Invalid string offset.");
                    var p = (int)position;
                    int Length8() { if (p >= end) throw new InvalidDataException("Truncated string."); var n = data[p++]; if ((n & 128) == 0) return n; if (p >= end) throw new InvalidDataException("Truncated string."); return ((n & 127) << 8) | data[p++]; }
                    int Length16() { if (p > end - 2) throw new InvalidDataException("Truncated string."); var n = U16(p); p += 2; if ((n & 32768) == 0) return n; if (p > end - 2) throw new InvalidDataException("Truncated string."); var low = U16(p); p += 2; return checked(((n & 32767) << 16) | low); }
                    if ((flags & 256) != 0)
                    {
                        _ = Length8(); var length = Length8();
                        if (length > end - p - 1) throw new InvalidDataException("Truncated UTF-8 string.");
                        strings[i] = Encoding.UTF8.GetString(data, p, length);
                    }
                    else
                    {
                        var length = Length16();
                        if (length > (end - p - 2) / 2) throw new InvalidDataException("Truncated UTF-16 string.");
                        strings[i] = Encoding.Unicode.GetString(data, p, length * 2);
                    }
                }
            }
            else if (type == 0x102)
            {
                if (header < 16 || size < header + 20) throw new InvalidDataException("Invalid element.");
                string Get(int index) => index >= 0 && index < strings.Length ? strings[index] : throw new InvalidDataException("Invalid manifest string reference.");
                var element = offset + header;
                if (Get(I32(element + 4)) == "manifest")
                {
                    var start = U16(element + 8); var stride = U16(element + 10); var count = U16(element + 12);
                    if (start < 20 || stride < 20 || start > end - element || count > (end - element - start) / stride) throw new InvalidDataException("Invalid attribute table.");
                    for (int i = 0; i < count; i++)
                    {
                        var a = element + start + i * stride;
                        if (Get(I32(a + 4)) != "package") continue;
                        var raw = I32(a + 8);
                        if (raw >= 0) return Safety.Package(Get(raw));
                        if (data[a + 15] == 3) return Safety.Package(Get(I32(a + 16)));
                    }
                }
            }
            offset = end;
        }
        throw new InvalidDataException("No package identifier in APK manifest.");
    }
}
