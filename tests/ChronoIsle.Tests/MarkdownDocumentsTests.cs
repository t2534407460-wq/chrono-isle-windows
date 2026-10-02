using ChronoIsle.App.Services.Markdown;

namespace ChronoIsle.Tests;

public sealed class MarkdownDocumentsTests
{
    [Fact]
    public void Image_links_resolve_encoded_attachment_paths_relative_to_the_note()
    {
        var origin = new Uri(@"C:\库\笔记\子目录\示例.md");
        var image = MarkdownDocuments.Resolve("../../%E9%99%84%E4%BB%B6/%E5%9B%BE%20%23%20%25.png", origin);
        Assert.Equal(@"C:\库\附件\图 # %.png", image.LocalPath);
    }

    [Fact]
    public async Task Source_links_roundtrip_spaces_unicode_brackets_and_line_anchor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronoisle-markdown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "中文 [测试] # %.md");
        try
        {
            await File.WriteAllTextAsync(path, "# 标题\n\n内容");
            var uri = MarkdownDocuments.Resolve(MarkdownDocuments.SourceLink(path, 3), null);
            Assert.Equal(path, uri.LocalPath); Assert.Equal("#L3", uri.Fragment);
            Assert.Equal("# 标题\n\n内容", await MarkdownDocuments.ReadAsync(uri));
            Assert.Equal(Path.Combine(directory, "第二页.md"), MarkdownDocuments.Resolve("第二页.md#标题", uri).LocalPath);
            Assert.Equal("#标题", Uri.UnescapeDataString(MarkdownDocuments.Resolve("#标题", uri).Fragment));
            File.WriteAllBytes(path, new byte[MarkdownDocuments.MaxBytes + 1]);
            await Assert.ThrowsAsync<IOException>(() => MarkdownDocuments.ReadAsync(uri));
            File.Delete(path);
            await Assert.ThrowsAsync<FileNotFoundException>(() => MarkdownDocuments.ReadAsync(uri));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/test.exe")]
    [InlineData("file://server/share/test.md")]
    [InlineData("obsidian://open?vault=test")]
    public void Links_cannot_launch_other_protocols_or_local_executables(string address) => Assert.Throws<IOException>(() => MarkdownDocuments.Resolve(address, null));

    [Fact]
    public void Legacy_source_lines_become_links_without_modifying_code()
    {
        var result = MarkdownDocuments.Prepare("[S4] E:\\Obsidian Vault\\示例.md · 第 62–65 行 · 标题\n`items[0]`");
        Assert.Contains("#L62)", result); Assert.Contains("`items[0]`", result);
    }
}
