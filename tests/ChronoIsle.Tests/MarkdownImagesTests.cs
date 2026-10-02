using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsle.App.Services.Markdown;

namespace ChronoIsle.Tests;

public sealed class MarkdownImagesTests
{
    [Fact]
    public async Task Images_decode_off_thread_resize_thumbnails_and_release_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            File.WriteAllBytes(path, Png());
            var image = await MarkdownImages.ReadAsync(new Uri(path));
            Assert.True(image.IsFrozen); Assert.Equal(1600, image.PixelWidth); Assert.Equal(800, image.PixelHeight);
            var thumbnail = await MarkdownImages.ReadAsync(new Uri(path), true);
            Assert.Equal(1200, thumbnail.PixelWidth); Assert.Equal(600, thumbnail.PixelHeight);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path);
            var pixels = new byte[4]; image.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixels, 4, 0);
            await Assert.ThrowsAsync<FileNotFoundException>(() => MarkdownImages.ReadAsync(new Uri(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Damaged_oversized_and_cancelled_reads_are_reported()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            await File.WriteAllTextAsync(path, "not an image");
            await Assert.ThrowsAsync<IOException>(() => MarkdownImages.ReadAsync(new Uri(path)));
            using (var file = File.Create(path)) file.SetLength(MarkdownImages.MaxBytes + 1L);
            await Assert.ThrowsAsync<IOException>(() => MarkdownImages.ReadAsync(new Uri(path)));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarkdownImages.ReadAsync(new Uri(path), token: cancellation.Token));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("file:///C:/run.exe", null)]
    [InlineData("file://server/share/image.png", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/private.png", "https://example.com/note.md")]
    public void Image_links_keep_protocol_and_remote_document_boundaries(string target, string? origin)
        => Assert.Throws<IOException>(() => MarkdownImages.Resolve(target, origin is null ? null : new Uri(origin)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_images_decode_or_reject_oversize_before_reading_the_body(bool oversized)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var data = oversized ? Array.Empty<byte>() : Png();
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {(oversized ? MarkdownImages.MaxBytes + 1 : data.Length)}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header); await stream.WriteAsync(data);
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var uri = new Uri($"http://127.0.0.1:{port}/image.png");
            if (oversized) await Assert.ThrowsAsync<IOException>(() => MarkdownImages.ReadAsync(uri, token: timeout.Token));
            else Assert.Equal(1600, (await MarkdownImages.ReadAsync(uri, token: timeout.Token)).PixelWidth);
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Cancelling_during_a_network_body_stops_the_read()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var headersSent = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 1000\r\n\r\n"));
            headersSent.SetResult(); await release.Task;
        });
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var load = MarkdownImages.ReadAsync(new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/slow.png"), token: cancel.Token);
            await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.TrySetResult(); listener.Stop(); await serve.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    static byte[] Png()
    {
        var bitmap = BitmapSource.Create(1600, 800, 96, 96, PixelFormats.Bgra32, null, new byte[1600 * 800 * 4], 1600 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
