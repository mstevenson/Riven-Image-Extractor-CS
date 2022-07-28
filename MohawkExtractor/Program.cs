using System.Buffers.Binary;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

public class MohawkExtractor
{
    private static int[] BPP = {1, 4, 8, 16, 24};

    private static int NONE = 0;
    private static int RLE8 = 1;
    private static int RLE_OTHER = 3;

    private static int LZ = 1;
    private static int LZ_OTHER = 2;
    private static int RIVEN = 4;

    public static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: MohawkExtractor <inputPath> <outputPath>");
            return;
        }
        var rootPath = args[0];
        if (!Directory.Exists(args[0]))
        {
            Console.WriteLine($"Can't find root path: {rootPath}");
            return;
        }
        var outputPath = args[1];
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.mhk", SearchOption.AllDirectories))
        {
            Extract(file, outputPath);
        }
    }

    private static void Extract(string archive, string outputPath)
    {
        var archiveName = Path.GetFileName(archive);
        Console.WriteLine(archiveName);
        var outputDir = Path.Combine(outputPath, Path.GetFileNameWithoutExtension(archive));
        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        Console.Write($"Output directory: {outputDir}");
        Span<byte> baseSpan = File.ReadAllBytes(archive);
        var s = baseSpan[..];
        var signature = GetBytes(ref s, 4); // "MHWK" signature
        var size = GetInt(ref s);
        // baseSpan = s;
        var rsrc = GetBytes(ref s, 4); // "RSRC"
        var version = GetShort(ref s); // version (0x100)
        GetShort(ref s); // unused
        size = GetInt(ref s); // size again

        int resourceDirOffset = GetInt(ref s);
        int fileTableOffset = GetUShort(ref s);
        int fileTableLength = GetUShort(ref s);
        
        var resourceTypes = new Dictionary<string, TypeInfo>();
        SetPosition(in baseSpan, out s, resourceDirOffset);
        int resourceNameListOffset = GetUShort(ref s);
        int typeNameCount = GetUShort(ref s);
        for (int i = 0; i < typeNameCount; i++)
        {
            var typeBytes = GetBytes(ref s, 4);
            var type = BytesToString(typeBytes);
            resourceTypes[type] = new TypeInfo(GetUShort(ref s), GetUShort(ref s));
        }

        foreach (var (type, typeInfo) in resourceTypes)
        {
            SetPosition(in baseSpan, out s, resourceDirOffset + typeInfo.ResourceTableOffset);
            int resourceCount = GetUShort(ref s);
            for (int i = 0; i < resourceCount; i++)
            {
                int resourceId = GetUShort(ref s);
                int resourceIndexInFileTable = GetUShort(ref s);
                typeInfo.ResourceIndexToResourceId[resourceIndexInFileTable] = resourceId;
                typeInfo.Resources[resourceId] = new ResourceInfo(type, resourceIndexInFileTable - 1);
            }

            SetPosition(in baseSpan, out s, resourceDirOffset + resourceTypes[type].NameTableOffset);
            int nameCount = GetUShort(ref s);
            for (int i = 0; i < nameCount; i++)
            {
                int nameListOffset = GetUShort(ref s);
                int resourceIndexInFileTable = GetUShort(ref s);
                var spanTemp = s;
                SetPosition(in baseSpan, out s, resourceDirOffset + resourceNameListOffset + nameListOffset);
                var ch = GetByte(ref s);
                var name = "";
                while (ch != 0)
                {
                    name += (char)ch;
                    ch = GetByte(ref s);
                }
                s = spanTemp; // reset span to original position
                int resourceId = typeInfo.ResourceIndexToResourceId[resourceIndexInFileTable];
                typeInfo.Resources[resourceId].Name = name;
            }
        }

        SetPosition(in baseSpan, out s, resourceDirOffset + fileTableOffset);
        var fileCount = GetInt(ref s);
        var files = new FileInfo[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            int offset = GetInt(ref s);
            if (i > 0 )
            {
                files[i - 1].Size = offset - files[i - 1].Offset;
            }
            int size0 = GetUShort(ref s);
            int size1 = GetByte(ref s);
            int size2 = GetByte(ref s) & 7;
            int fileSize = size0 | (size1 << 16) | (size2 << 24);
            files[i] = new FileInfo(offset, fileSize);
            GetShort(ref s);
        }

        foreach (var (type, typeInfo) in resourceTypes)
        {
            if (type == "tBMP")
            {
                foreach (var (resourceId, resource) in typeInfo.Resources)
                {
                    FileInfo file = files[resource.FileTableOffset];
                    string outputFile = $"{outputDir}/{type}/{resourceId}.png";
                    if (File.Exists(outputFile))
                    {
                        continue;
                    }
                    var dir = Path.GetDirectoryName(outputFile);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    Console.WriteLine($"{resourceId} - {resource}");
                    SetPosition(in baseSpan, out s, file.Offset);
                    
                    int width = GetUShort(ref s) & 0x3ff;
                    int height = GetUShort(ref s) & 0x3ff;
                    int bytesPerRow = GetUShort(ref s) & 0x3fe;
                    int compression = GetUShort(ref s);
                    int bpp = BPP[compression & 0b111];
                    int secondaryCompression = (compression & 0b11110000) >> 4;
                    int primaryCompression = (compression & 0b111100000000) >> 8;
                    if (secondaryCompression != NONE)
                    {
                        throw new ArgumentException("unsupported secondary compression: " + secondaryCompression);
                    }
                    if (primaryCompression != NONE && primaryCompression != RIVEN)
                    {
                        throw new ArgumentException("unsupported primary compression: " + primaryCompression);
                    }

                    if (bpp == 24)
                    {
                        var image = new Image<Rgb24>(width, height);
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                byte b = GetByte(ref s);
                                byte g = GetByte(ref s);
                                byte r = GetByte(ref s);
                                image[x, y] = new Rgb24(r, g, b);
                            }
                            for (int i = 0; i < width; i++)
                            {
                                GetByte(ref s);
                            }
                        }
                        WritePng(image, outputFile);
                        continue;
                    }

                    GetShort(ref s);
                    GetByte(ref s); // bits per color, always 24
                    int colorCount = GetByte(ref s) + 1;
                    var colors = new Rgb24[colorCount];
                    for (int i = 0; i < colorCount; i++)
                    {
                        byte b = GetByte(ref s);
                        byte g = GetByte(ref s);
                        byte r = GetByte(ref s);
                        colors[i] = new Rgb24(r, g, b);
                    }
                    if (primaryCompression == NONE)
                    {
                        var image = new Image<Rgb24>(width, height);
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < bytesPerRow; x++)
                            {
                                int colorIndex = GetByte(ref s);
                                if (x < width)
                                {
                                    image[x, y] = colors[colorIndex];
                                }
                            }
                        }
                        WritePng(image, outputFile);
                    }
                    else
                    {
                        GetInt(ref s); // unknown
                        int dataLength = file.Offset + file.Size - GetPosition(in baseSpan, in s);
                        var data = s[..dataLength];
                        int[] image = new int[bytesPerRow * height];
                        int p = 0;
                        int q = 0;
                        while (p < data.Length)
                        {
                            byte cmd = data[p];
                            p++;
                            if (cmd == 0)
                            {
                                // End of stream: when reaching it, the decoding is complete.
                                break;
                            }
                            else if (cmd <= 0x3f)
                            {
                                // Output n pixel duplets, where n is the command value itself. Pixel data comes
                                // immediately after the command as 2*n bytes representing direct indices in the 8-bit
                                // color table.
                                for (int i1 = 0; i1 < cmd; i1++)
                                {
                                    image[q] = data[p];
                                    image[q + 1] = data[p + 1];
                                    p += 2;
                                    q += 2;
                                }
                            }
                            else if (cmd <= 0x7f)
                            {
                                // Repeat last 2 pixels n times, where n = command_value & 0x3F.
                                int pixel1 = image[q - 2];
                                int pixel2 = image[q - 1];
                                for (int i2 = 0; i2 < (cmd & 0x3f); i2++)
                                {
                                    image[q] = pixel1;
                                    image[q + 1] = pixel2;
                                    q += 2;
                                }
                            }
                            else if (cmd <= 0xbf)
                            {
                                // Repeat last 4 pixels n times, where n = command_value & 0x3F.
                                int pixel1 = image[q - 4];
                                int pixel2 = image[q - 3];
                                int pixel3 = image[q - 2];
                                int pixel4 = image[q - 1];
                                for (int i3 = 0; i3 < (cmd & 0x3f); i3++)
                                {
                                    image[q] = pixel1;
                                    image[q + 1] = pixel2;
                                    image[q + 2] = pixel3;
                                    image[q + 3] = pixel4;
                                    q += 4;
                                }
                            }
                            else
                            {
                                // Begin of a subcommand stream. This is like the main command stream, but contains
                                // another set of commands which are somewhat more specific and a bit more complex.
                                // This command says that command_value & 0x3F subcommands will follow.
                                int subCount = cmd & 0x3f;
                                for (int i4 = 0; i4 < subCount; i4++)
                                {
                                    int sub = data[p];
                                    p++;
                                    if (sub is >= 0x01 and <= 0x0f)
                                    {
                                        // 0000mmmm
                                        // Repeat duplet at relative position -m, where m is given in duplets. So if m=1,
                                        // repeat the last duplet.
                                        int offset = -(sub & 0b00001111) * 2;
                                        image[q] = image[q + offset];
                                        image[q + 1] = image[q + offset + 1];
                                        q += 2;
                                    }
                                    else if (sub == 0x10)
                                    {
                                        // Repeat last duplet, but change second pixel to p.
                                        image[q] = image[q - 2];
                                        image[q + 1] = data[p];
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is >= 0x11 and <= 0x1f)
                                    {
                                        // 0001mmmm
                                        // Output the first pixel of last duplet, then pixel at relative position -m. m is
                                        // given in pixels. (relative to the second pixel!)
                                        int offset = -(sub & 0b00001111) + 1;
                                        image[q] = image[q - 2];
                                        image[q + 1] = image[q + offset];
                                        q += 2;
                                    }
                                    else if (sub is >= 0x20 and <= 0x2f)
                                    {
                                        // 0010xxxx
                                        // Repeat last duplet, but add x to second pixel.
                                        image[q] = image[q - 2];
                                        image[q + 1] = image[q - 1] + (sub & 0b00001111);
                                        q += 2;
                                    }
                                    else if (sub is >= 0x30 and <= 0x3f)
                                    {
                                        // 0011xxxx
                                        // Repeat last duplet, but subtract x from second pixel.
                                        image[q] = image[q - 2];
                                        image[q + 1] = image[q - 1] - (sub & 0b00001111);
                                        q += 2;
                                    }
                                    else if (sub == 0x40)
                                    {
                                        // Repeat last duplet, but change first pixel to p.
                                        image[q] = data[p];
                                        image[q + 1] = image[q - 1];
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is >= 0x41 and <= 0x4f)
                                    {
                                        // 0100mmmm
                                        // Output pixel at relative position -m, then second pixel of last duplet.
                                        int offset = -(sub & 0b00001111);
                                        image[q] = image[q + offset];
                                        image[q + 1] = image[q - 1];
                                        q += 2;
                                    }
                                    else if (sub == 0x50)
                                    {
                                        // Output two absolute pixel values, p1 and p2.
                                        image[q] = data[p];
                                        image[q + 1] = data[p + 1];
                                        p += 2;
                                        q += 2;
                                    }
                                    else if (sub is >= 0x51 and <= 0x57)
                                    {
                                        // 01010mmm p
                                        // Output pixel at relative position -m, then absolute pixel value p.
                                        int offset = -(sub & 0b00000111);
                                        image[q] = image[q + offset];
                                        image[q + 1] = data[p];
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is >= 0x59 and <= 0x5f)
                                    {
                                        // 01011mmm p
                                        // Output absolute pixel value p, then pixel at relative position -m.
                                        // (relative to the second pixel!)
                                        int offset = -(sub & 0b00000111) + 1;
                                        image[q] = data[p];
                                        image[q + 1] = image[q + offset];
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is >= 0x60 and <= 0x6f)
                                    {
                                        // 0110xxxx p
                                        // Output absolute pixel value p, then (second pixel of last duplet) + x.
                                        image[q] = data[p];
                                        image[q + 1] = image[q - 1] + (sub & 0b00001111);
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is >= 0x70 and <= 0x7f)
                                    {
                                        // 0111xxxx p
                                        // Output absolute pixel value p, then (second pixel of last duplet) - x.
                                        image[q] = data[p];
                                        image[q + 1] = image[q - 1] - (sub & 0b00001111);
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is >= 0x80 and <= 0x8f)
                                    {
                                        // 1000xxxx
                                        // Repeat last duplet adding x to the first pixel.
                                        image[q] = image[q - 2] + (sub & 0b00001111);
                                        image[q + 1] = image[q - 1];
                                        q += 2;
                                    }
                                    else if (sub is >= 0x90 and <= 0x9f)
                                    {
                                        // 1001xxxx p
                                        // Output (first pixel of last duplet) + x, then absolute pixel value p.
                                        image[q] = image[q - 2] + (sub & 0b00001111);
                                        image[q + 1] = data[p];
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub == 0xa0)
                                    {
                                        // 0xa0 xxxxyyyy
                                        // Repeat last duplet, adding x to the first pixel and y to the second.
                                        int x = (data[p] & 0b11110000) >> 4;
                                        int y = data[p] & 0b00001111;
                                        image[q] = image[q - 2] + x;
                                        image[q + 1] = image[q - 1] + y;
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub == 0xb0)
                                    {
                                        // 0xb0 xxxxyyyy
                                        // Repeat last duplet, adding x to the first pixel and subtracting y to the
                                        // second.
                                        int x = (data[p] & 0b11110000) >> 4;
                                        int y = data[p] & 0b00001111;
                                        image[q] = image[q - 2] + x;
                                        image[q + 1] = image[q - 1] - y;
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is >= 0xc0 and <= 0xcf)
                                    {
                                        // 1100xxxx
                                        // Repeat last duplet subtracting x from first pixel.
                                        image[q] = image[q - 2] - (sub & 0b00001111);
                                        image[q + 1] = image[q - 1];
                                        q += 2;
                                    }
                                    else if (sub is >= 0xd0 and <= 0xdf)
                                    {
                                        // 1101xxxx p
                                        // Output (first pixel of last duplet) - x, then absolute pixel value p.
                                        image[q] = image[q - 2] - (sub & 0b00001111);
                                        image[q + 1] = data[p];
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub == 0xe0)
                                    {
                                        // 0xe0 xxxxyyyy
                                        // Repeat last duplet, subtracting x from first pixel and adding y to second.
                                        int x = (data[p] & 0b11110000) >> 4;
                                        int y = data[p] & 0b00001111;
                                        image[q] = image[q - 2] - x;
                                        image[q + 1] = image[q - 1] + y;
                                        p++;
                                        q += 2;
                                    }
                                    else if (sub is 0xf0 or 0xff)
                                    {
                                        // 0xfx xxxxyyyy
                                        // Repeat last duplet, subtracting x from first pixel and y from second.
                                        int x = ((sub & 0b00001111) << 4) | ((data[p] & 0b11110000) >> 4);
                                        int y = data[p] & 0b00001111;
                                        image[q] = image[q - 2] - x;
                                        image[q + 1] = image[q - 1] - y;
                                        p++;
                                        q += 2;
                                    }
                                    else if ((sub & 0b10100000) == 0b10100000 && sub != 0xfc)
                                    {
                                        // 1x1xxxmm mmmmmmmm
                                        // Repeat n duplets from relative position -m (given in pixels, not duplets). If r
                                        // is 0, another byte follows and the last pixel is set to that value. n and r come
                                        // from the table on the right.
                                        int n, r;
                                        if (sub is >= 0xa4 and <= 0xa7) {
                                            n = 2;
                                            r = 0;
                                        } else if (sub is >= 0xa8 and <= 0xab) {
                                            n = 2;
                                            r = 1;
                                        } else if (sub is >= 0xac and <= 0xaf) {
                                            n = 3;
                                            r = 0;
                                        } else if (sub is >= 0xb4 and <= 0xb7) {
                                            n = 3;
                                            r = 1;
                                        } else if (sub is >= 0xb8 and <= 0xbb) {
                                            n = 4;
                                            r = 0;
                                        } else if (sub is >= 0xbc and <= 0xbf) {
                                            n = 4;
                                            r = 1;
                                        } else if (sub is >= 0xe4 and <= 0xe7) {
                                            n = 5;
                                            r = 0;
                                        } else if (sub is >= 0xe8 and <= 0xeb) {
                                            n = 5;
                                            r = 1;
                                        } else if (sub is >= 0xec and <= 0xef) {
                                            n = 6;
                                            r = 0;
                                        } else if (sub is >= 0xf4 and <= 0xf7) {
                                            n = 6;
                                            r = 1;
                                        } else if (sub is >= 0xf8 and <= 0xfb) {
                                            n = 7;
                                            r = 0;
                                        } else {
                                            throw new Exception("subcommand: " + sub);
                                        }

                                        int offset = -(data[p] | ((sub & 0b00000011) << 8));
                                        p++;
                                        for (int j = 0; j < n; j++) {
                                            image[q + 2 * j] = image[q + offset + 2 * j];
                                            image[q + 2 * j + 1] = image[q + offset + 2 * j + 1];
                                        }
                                        q += 2 * n;
                                        if (r == 0) {
                                            image[q - 1] = data[p];
                                            p++;
                                        }
                                    } else if (sub == 0xfc) {
                                        // 0xfc nnnnnrmm mmmmmmmm (p)
                                        // Repeat n+2 duplets from relative position -m (given in pixels, not duplets). If
                                        // r is 0, another byte p follows and the last pixel is set to absolute value p.
                                        int n = (data[p] & 0b11111000) >> 3;
                                        int r = (data[p] & 0b00000100) >> 2;
                                        int offset = -(data[p + 1] | ((data[p] & 0b00000011) << 8));

                                        for (int j = 0; j < n + 2; j++) {
                                            image[q + 2 * j] = image[q + offset + 2 * j];
                                            image[q + 2 * j + 1] = image[q + offset + 2 * j + 1];
                                        }
                                        p += 2;
                                        q += 2 * n + 4;
                                        if (r == 0) {
                                            image[q - 1] = data[p];
                                            p++;
                                        }
                                    } else {
                                        throw new Exception("subcommand: " + sub);
                                    }
                                }
                            }
                        }
                        var output = new Image<Rgb24>(width, height);
                        int i = 0;
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < bytesPerRow; x++)
                            {
                                int colorIndex = image[i];
                                Rgb24 color = colors[colorIndex & 0xff];
                                if (x < width)
                                {
                                    output[x, y] = color;
                                }
                                i++;
                            }
                        }
                        WritePng(output, outputFile);
                    }
                }
            }
            else if (type == "tMOV")
            {
                foreach (var (resourceId, resource) in typeInfo.Resources)
                {
                    FileInfo file = files[resource.FileTableOffset];
                    string outputFile = $"{outputDir}/{type}/{resourceId}.mov";
                    if (File.Exists(outputFile))
                    {
                        continue;
                    }
                    var dir = Path.GetDirectoryName(outputFile);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    SetPosition(in baseSpan, out s, file.Offset);

                    var fileBytes = s[..file.Size];
                    List<int> stcoOffsets = Find(fileBytes, new byte[]{0x73, 0x74, 0x63, 0x6f, 0x00, 0x00, 0x00, 0x00});
                    Span<byte> movBuffer = fileBytes;

                    if (stcoOffsets.Count == 0)
                    {
                        Console.WriteLine($"{resourceId} {resource} ");
                    }
                    else
                    {
                        foreach (var stcoOffset in stcoOffsets)
                        {
                            SetPosition(in fileBytes, out movBuffer, stcoOffset);
                            GetInt(ref movBuffer); // 'stco'
                            GetByte(ref movBuffer); // version;
                            GetByte(ref movBuffer); // flags
                            GetByte(ref movBuffer);
                            GetByte(ref movBuffer);
                            int entryCount = GetInt(ref movBuffer);

                            // http://insidethelink.ortiche.net/wiki/index.php/Riven_tMOV_resources
                            // "Note that all offsets in the stco chunk are absolute offsets in the Mohawk archive, not offsets within the video as the format specifies."
                            for (int i = 0; i < entryCount; i++)
                            {
                                int offset = BinaryPrimitives.ReadInt32BigEndian(movBuffer) - file.Offset;
                                SetInt(ref movBuffer, offset);
                            }
                        }
                    }
                    
                    using var f = File.Create(outputFile);
                    f.Write(fileBytes);
                }
            }
        }
    }
    
    private static List<int> Find(in ReadOnlySpan<byte> array, in ReadOnlySpan<byte> target)
    {
        var result = new List<int>();
        if (target.Length == 0)
        {
            return result;
        }

        for (int i = 0; i < array.Length - target.Length + 1; i++)
        {
            bool found = true;
            for (int j = 0; j < target.Length; j++)
            {
                if (array[i + j] != target[j])
                {
                    found = false;
                    break;
                }
            }
            if (found)
            {
                result.Add(i);
            }
        }
        return result;
    }
    
    private class TypeInfo
    {
        public readonly int ResourceTableOffset;
        public readonly int NameTableOffset;
        public readonly Dictionary<int, int> ResourceIndexToResourceId = new();
        public readonly Dictionary<int, ResourceInfo> Resources = new();

        public TypeInfo(int resourceTableOffset, int nameTableOffset)
        {
            ResourceTableOffset = resourceTableOffset;
            NameTableOffset = nameTableOffset;
        }
        
        public override string ToString()
        {
            return $"{ResourceTableOffset} {NameTableOffset}";
        }
    }
    private class ResourceInfo
    {
        public readonly string Type;
        public readonly int FileTableOffset;
        public string Name;

        public ResourceInfo(string type, int fileTableOffset)
        {
            Type = type;
            FileTableOffset = fileTableOffset;
        }

        public override string ToString() => $"Type: {Type} Offset: {FileTableOffset} Name: {Name}";
    }

    private class FileInfo
    {
        public int Offset { get; }
        public int Size { get; set; }

        public FileInfo(int offset, int size)
        {
            Offset = offset;
            Size = size;
        }
        
        public override string ToString() => $"Offset: {Offset} Size: {Size}";
    }

    private static readonly PngEncoder Encoder = new()
    {
        BitDepth = PngBitDepth.Bit8,
        ColorType = PngColorType.Rgb
    };
    
    private static void WritePng(Image image, string path)
    {
        using var stream = File.Create(path);
        image.SaveAsPng(stream, Encoder);
    }
    
    private static void SetPosition(in Span<byte> source, out Span<byte> target, int offset)
    {
        target = source[offset..];
    }

    private static int GetPosition(in Span<byte> fullSpan, in Span<byte> subSpan)
    {
        return fullSpan.Length - subSpan.Length;
    }
    
    private static int GetInt(ref Span<byte> span)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(span);
        span = span[sizeof(int)..];
        return value;
    }
    
    private static void SetInt(ref Span<byte> span, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        span = span[sizeof(int)..];
    }
    
    private static short GetShort(ref Span<byte> span)
    {
        var value = BinaryPrimitives.ReadInt16BigEndian(span);
        span = span[sizeof(short)..];
        return value;
    }
    
    private static ushort GetUShort(ref Span<byte> span)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(span);
        span = span[sizeof(short)..];
        return value;
    }
    
    private static byte GetByte(ref Span<byte> span)
    {
        var value = span[0];
        span = span[1..];
        return value;
    }
    
    private static Span<byte> GetBytes(ref Span<byte> span, int count)
    {
        var value = span[..count];
        span = span[count..];
        return value;
    }

    private static string BytesToString(Span<byte> bytes)
    {
        return Encoding.ASCII.GetString(bytes);
    }
}