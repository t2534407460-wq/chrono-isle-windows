using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using ChronoIsle.App.Services.Knowledge;

namespace ChronoIsle.App.Services.Markdown;

public static class MarkdownDocuments
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoLinks().UseEmphasisExtras().UseTaskLists().UseYamlFrontMatter().DisableHtml().Build();
    public const int MaxBytes = 2 * 1024 * 1024;
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string SourceLink(string path, int line) => new Uri(Path.GetFullPath(path)).AbsoluteUri.Replace("(", "%28").Replace(")", "%29") + "#L" + line;
    public static string Label(string value) => value.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("\r", " ").Replace("\n", " ");

    // Upgrade old saved answers at display time; never rewrite the user's conversation history.
    public static string Prepare(string markdown) => Regex.Replace(markdown,
        @"(?m)^\[(S\d+)\] ([A-Za-z]:\\[^\r\n]+?) · 第 (\d+)[–-](\d+) 行",
        m => $"[{m.Groups[1].Value} · {Label(Path.GetFileName(m.Groups[2].Value))}]({SourceLink(m.Groups[2].Value, int.Parse(m.Groups[3].Value))}) · 第 {m.Groups[3].Value}–{m.Groups[4].Value} 行",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    public static Uri Resolve(string target, Uri? origin)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 8192) throw new IOException("链接为空或过长。");
        // Canonical file URIs decode relative %23/%25 correctly, including files opened by the picker.
        if (origin is { IsFile: true }) origin = new Uri(origin.AbsoluteUri);
        Uri? uri;
        if (target.StartsWith('#') && origin is not null) uri = new Uri(origin, target);
        else if (!Uri.TryCreate(target, UriKind.Absolute, out uri))
        {
            if (origin is null) throw new IOException("该相对链接缺少来源文档，请先打开对应笔记。");
            uri = new Uri(origin, target);
        }
        if (uri.Scheme is "http" or "https") return uri;
        if (!uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host)) throw new IOException("仅支持本地 Markdown、图片文件及 HTTP/HTTPS 链接。");
        if (!IsMarkdown(uri) && !MarkdownImages.IsImage(uri)) throw new IOException("仅支持 Markdown 和 PNG、JPEG、GIF、BMP、TIFF、ICO 图片文件。");
        return uri;
    }

    public static bool IsMarkdown(Uri uri) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".md" or ".markdown";

    public static async Task<string> ReadAsync(Uri uri, CancellationToken token = default)
    {
        uri = Resolve(uri.AbsoluteUri, null);
        if (!IsMarkdown(uri)) throw new IOException("请提供 Markdown 文件或直接返回 Markdown 的 .md 链接。");
        if (uri.IsFile)
        {
            await using var file = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            return await ReadBounded(file, token);
        }
        using var response = await RemoteKnowledgeClient.TryReadDocumentAsync(uri, token) ?? await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media?.Contains("html", StringComparison.OrdinalIgnoreCase) == true) throw new IOException("链接返回网页，请使用浏览器打开，或选择 Markdown 原始文件链接。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await ReadBounded(stream, token);
    }

    static async Task<string> ReadBounded(Stream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (data.Length + count > MaxBytes) throw new IOException("文档超过 2 MiB，请拆分后查看。");
            data.Write(buffer, 0, count);
        }
        var bytes = data.ToArray();
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }
}
