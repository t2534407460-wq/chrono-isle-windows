using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using ChronoIsle.App.Services.Knowledge;

namespace ChronoIsle.App.Services.Markdown;

public static class MarkdownImages
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const long MaxPixels = 32_000_000;
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim Readers = new(4);

    public static bool IsImage(Uri uri) => IsImagePath(Uri.UnescapeDataString(uri.AbsolutePath));
    internal static bool IsImagePath(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".ico";

    public static Uri Resolve(string address, Uri? origin)
    {
        var uri = MarkdownDocuments.Resolve(address, origin);
        if (!IsImage(uri)) throw new IOException("不支持此图片格式，支持 PNG、JPEG、GIF、BMP、TIFF、ICO。");
        if (origin is { IsFile: false } && uri.IsFile) throw new IOException("网络文档不能读取本地图片。");
        return uri;
    }

    // Both I/O and decoding run away from the UI thread. Frozen bitmaps do not hold file handles.
    public static Task<BitmapSource> ReadAsync(Uri uri, bool thumbnail = false, CancellationToken token = default) => Task.Run(async () =>
    {
        uri = Resolve(uri.AbsoluteUri, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20)); token = timeout.Token;
        await Readers.WaitAsync(token);
        try
        {
            byte[] bytes;
            if (uri.IsFile)
            {
                await using var file = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
                if (file.Length > MaxBytes) throw new IOException("图片超过 16 MiB，无法预览。");
                bytes = await ReadBounded(file, token);
            }
            else
            {
                using var response = await RemoteKnowledgeClient.TryReadDocumentAsync(uri, token) ?? await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("图片超过 16 MiB，无法预览。");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                bytes = await ReadBounded(stream, token);
            }
            token.ThrowIfCancellationRequested();
            using var data = new MemoryStream(bytes, false);
            var frame = BitmapDecoder.Create(data, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
                throw new IOException("图片尺寸过大，无法预览（上限 3200 万像素）。");
            data.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = data;
            if (thumbnail && Math.Max(frame.PixelWidth, frame.PixelHeight) > 1200)
            {
                if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = 1200;
                else bitmap.DecodePixelHeight = 1200;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            token.ThrowIfCancellationRequested();
            return (BitmapSource)bitmap;
        }
        catch (Exception error) when (error is NotSupportedException or System.IO.FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { throw new IOException("图片已损坏或无法解码。", error); }
        finally { Readers.Release(); }
    }, token);

    static async Task<byte[]> ReadBounded(Stream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (data.Length + count > MaxBytes) throw new IOException("图片超过 16 MiB，无法预览。");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }
}
