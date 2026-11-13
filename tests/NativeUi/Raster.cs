using System;
using System.IO;
using System.IO.Compression;
using Brutal.ImGuiApi;

internal static class Raster
{
    public static unsafe void Save(ImDrawDataPtr data, string path, int width, int height, int originX = 0, int originY = 0)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 10; pixels[i + 1] = 14; pixels[i + 2] = 20; pixels[i + 3] = 255;
        }
        foreach (ImDrawListPtr list in data.CmdLists.Span)
        {
            var vertices = list.VtxBuffer.Span;
            var indices = list.IdxBuffer.Span;
            foreach (var command in list.CmdBuffer.Span)
            {
                if (command.UserCallback != IntPtr.Zero) continue;
                var texture = command.TexRef._TexData;
                if (texture.IsNull()) texture = ImGui.GetIO().Fonts.TexData;
                byte* tex = texture.IsNull() ? null : texture.Pixels;
                int tw = texture.IsNull() ? 1 : texture.Width, th = texture.IsNull() ? 1 : texture.Height;
                int bpp = texture.IsNull() ? 1 : texture.BytesPerPixel;
                for (int i = (int)command.IdxOffset; i < command.IdxOffset + command.ElemCount; i += 3)
                {
                    var a = vertices[(int)command.VtxOffset + indices[i]];
                    var b = vertices[(int)command.VtxOffset + indices[i + 1]];
                    var c = vertices[(int)command.VtxOffset + indices[i + 2]];
                    var origin = new Brutal.Numerics.float2(originX, originY);
                    a.pos -= origin; b.pos -= origin; c.pos -= origin;
                    float denominator = (b.pos.Y - c.pos.Y) * (a.pos.X - c.pos.X) + (c.pos.X - b.pos.X) * (a.pos.Y - c.pos.Y);
                    if (Math.Abs(denominator) < 0.0001) continue;
                    int x0 = Math.Max(0, (int)Math.Max(command.ClipRect.X - originX, Math.Floor(Math.Min(a.pos.X, Math.Min(b.pos.X, c.pos.X)))));
                    int x1 = Math.Min(width - 1, (int)Math.Min(command.ClipRect.Z - originX - 1, Math.Ceiling(Math.Max(a.pos.X, Math.Max(b.pos.X, c.pos.X)))));
                    int y0 = Math.Max(0, (int)Math.Max(command.ClipRect.Y - originY, Math.Floor(Math.Min(a.pos.Y, Math.Min(b.pos.Y, c.pos.Y)))));
                    int y1 = Math.Min(height - 1, (int)Math.Min(command.ClipRect.W - originY - 1, Math.Ceiling(Math.Max(a.pos.Y, Math.Max(b.pos.Y, c.pos.Y)))));
                    for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float wa = ((b.pos.Y - c.pos.Y) * (px - c.pos.X) + (c.pos.X - b.pos.X) * (py - c.pos.Y)) / denominator;
                        float wb = ((c.pos.Y - a.pos.Y) * (px - c.pos.X) + (a.pos.X - c.pos.X) * (py - c.pos.Y)) / denominator;
                        float wc = 1 - wa - wb;
                        if (wa < 0 || wb < 0 || wc < 0) continue;
                        float u = a.uv.X * wa + b.uv.X * wb + c.uv.X * wc;
                        float v = a.uv.Y * wa + b.uv.Y * wb + c.uv.Y * wc;
                        float sampleX = Math.Clamp(u * tw - 0.5f, 0, tw - 1), sampleY = Math.Clamp(v * th - 0.5f, 0, th - 1);
                        int tx = (int)sampleX, ty = (int)sampleY;
                        float fx = sampleX - tx, fy = sampleY - ty;
                        float ta = tex == null ? 1 : Sample(tex, tw, th, bpp, tx, ty) * (1 - fx) * (1 - fy)
                            + Sample(tex, tw, th, bpp, tx + 1, ty) * fx * (1 - fy)
                            + Sample(tex, tw, th, bpp, tx, ty + 1) * (1 - fx) * fy
                            + Sample(tex, tw, th, bpp, tx + 1, ty + 1) * fx * fy;
                        float alpha = ((a.col >> 24) * wa + (b.col >> 24) * wb + (c.col >> 24) * wc) / 255 * ta;
                        int at = (y * width + x) * 4;
                        for (int channel = 0; channel < 3; channel++)
                        {
                            int shift = channel * 8;
                            float color = ((a.col >> shift) & 255) * wa + ((b.col >> shift) & 255) * wb + ((c.col >> shift) & 255) * wc;
                            pixels[at + channel] = (byte)Math.Clamp(color * alpha + pixels[at + channel] * (1 - alpha), 0, 255);
                        }
                    }
                }
            }
        }
        Png(path, pixels, width, height);
    }

    private static unsafe float Sample(byte* tex, int w, int h, int bpp, int x, int y)
        => tex[(Math.Min(h - 1, y) * w + Math.Min(w - 1, x)) * bpp + (bpp == 4 ? 3 : 0)] / 255f;

    private static void Png(string path, byte[] pixels, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        Put(header, 0, (uint)width); Put(header, 4, (uint)height);
        header[8] = 8; header[9] = 6;
        Chunk(file, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            for (int y = 0; y < height; y++)
            {
                zip.WriteByte(0);
                zip.Write(pixels, y * width * 4, width * 4);
            }
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream file, string type, byte[] bytes)
    {
        byte[] label = System.Text.Encoding.ASCII.GetBytes(type), size = new byte[4];
        Put(size, 0, (uint)bytes.Length); file.Write(size); file.Write(label); file.Write(bytes);
        uint crc = 0xffffffff;
        foreach (byte b in label) crc = Crc(crc, b);
        foreach (byte b in bytes) crc = Crc(crc, b);
        Put(size, 0, crc ^ 0xffffffff); file.Write(size);
    }
    private static uint Crc(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320 : 0);
        return crc;
    }
    private static void Put(byte[] bytes, int at, uint value)
    {
        bytes[at] = (byte)(value >> 24); bytes[at + 1] = (byte)(value >> 16);
        bytes[at + 2] = (byte)(value >> 8); bytes[at + 3] = (byte)value;
    }
}
