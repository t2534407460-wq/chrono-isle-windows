using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChronoIsle.App.Services.Knowledge;

public sealed record KnowledgeSource(string Id, string RelativePath, string Heading, int StartLine, int EndLine,
    string Text, string Metadata, DateTime ModifiedUtc, string ContentHash);

public sealed record KnowledgeSearchResult(string VaultPath, IReadOnlyList<KnowledgeSource> Sources,
    int NoteCount, int SkippedCount, bool Limited, string? SnapshotId = null, string? DocumentBaseUri = null);

/// <summary>Read-only, bounded local retrieval. A failed refresh never falls back to a stale snapshot.</summary>
public sealed class ObsidianKnowledgeIndex
{
    const int MaxFileBytes = 2 * 1024 * 1024;
    const int MaxCharacters = 16 * 1024 * 1024;
    const int MaxChunks = 20000;
    const int ChunkCharacters = 1800;
    readonly SemaphoreSlim gate = new(1, 1);
    Dictionary<string, Note> notes = new(VaultPaths.Comparer);
    string? indexedRoot;
    static readonly Regex Words = new(@"[\u3400-\u9fff]+|[a-z0-9_]+(?:\.[0-9]+)*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly string[] Noise = ["请问", "请帮我", "帮我", "告诉我", "知识库", "根据资料", "根据文档", "如何", "什么", "怎么", "哪些", "是否", "可以", "一下", "详细", "介绍", "说明"];

    public async Task<KnowledgeSearchResult> SearchAsync(string root, string question, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => Search(root, question, cancellationToken), cancellationToken); }
        finally { gate.Release(); }
    }

    KnowledgeSearchResult Search(string root, string question, CancellationToken token)
    {
        root = VaultPaths.ValidateRoot(root);
        if (!string.Equals(root, indexedRoot, VaultPaths.Comparison)) { notes.Clear(); indexedRoot = root; }
        var refreshed = new Dictionary<string, Note>(VaultPaths.Comparer);
        var directories = new Stack<string>();
        directories.Push(root);
        var skipped = 0;
        var limited = false;
        var characters = 0;
        var chunks = 0;
        var entries = 0;
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 100000) { limited = true; break; }
                    var name = Path.GetFileName(path);
                    if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                        if (!Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
                        var info = new FileInfo(path);
                        if (info.Length > MaxFileBytes) { skipped++; continue; }
                        if (!notes.TryGetValue(path, out var note) || note.Length != info.Length || note.ModifiedUtc != info.LastWriteTimeUtc)
                            note = ReadNote(root, path, token);
                        if (characters + note.Characters > MaxCharacters || chunks + note.Chunks.Count > MaxChunks || refreshed.Count >= 20000)
                        { limited = true; break; }
                        refreshed.Add(path, note);
                        characters += note.Characters;
                        chunks += note.Chunks.Count;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { skipped++; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (directory == root) throw new KnowledgeBaseException("无法读取知识库目录，请检查磁盘和读取权限。");
                skipped++;
            }
            if (limited) break;
        }
        token.ThrowIfCancellationRequested();
        notes = refreshed; // Deleted, unreadable and oversized files are removed, even on a partial scan.
        var query = Terms(question, true).Distinct(StringComparer.Ordinal).Take(80).ToArray();
        if (query.Length == 0) return new(root, [], notes.Count, skipped, limited);
        var candidates = notes.Values.SelectMany(n => n.Chunks).ToArray();
        var frequencies = query.ToDictionary(t => t, t => candidates.Count(c => c.Terms.ContainsKey(t)));
        var average = candidates.Length == 0 ? 1 : candidates.Average(c => c.WordCount);
        var ranked = new List<(Chunk Chunk, double Score)>();
        foreach (var chunk in candidates)
        {
            token.ThrowIfCancellationRequested();
            var score = 0d;
            var matches = 0;
            foreach (var term in query)
            {
                if (!chunk.Terms.TryGetValue(term, out var count)) continue;
                matches++;
                var idf = Math.Log(1 + (candidates.Length - frequencies[term] + .5) / (frequencies[term] + .5));
                score += idf * count * 2.2 / (count + 1.2 * (.25 + .75 * chunk.WordCount / Math.Max(1, average)));
            }
            // A shared generic word is not enough evidence for a long, unrelated question.
            if (matches < Math.Min(2, query.Length)) continue;
            ranked.Add((chunk, score * ((double)matches / query.Length)));
        }
        var selected = new List<KnowledgeSource>();
        var perNote = new Dictionary<string, int>(VaultPaths.Comparer);
        foreach (var item in ranked.OrderByDescending(r => r.Score).ThenBy(r => r.Chunk.Source.RelativePath, StringComparer.Ordinal).ThenBy(r => r.Chunk.Source.StartLine))
        {
            var source = item.Chunk.Source;
            var count = perNote.GetValueOrDefault(source.RelativePath);
            if (count >= 2) continue;
            selected.Add(source with { Id = $"S{selected.Count + 1}" });
            perNote[source.RelativePath] = count + 1;
            if (selected.Count == 6) break;
        }
        return new(root, selected, notes.Count, skipped, limited);
    }

    public async Task<bool> SourcesUnchangedAsync(KnowledgeSearchResult result, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var unchanged = await Task.Run(() => SourcesUnchanged(result, token), token);
            // Metadata can remain identical after an editor restores a file. Force a fresh read on retry.
            if (!unchanged && string.Equals(indexedRoot, result.VaultPath, VaultPaths.Comparison))
                foreach (var source in result.Sources) notes.Remove(Path.Combine(result.VaultPath, source.RelativePath));
            return unchanged;
        }
        finally { gate.Release(); }
    }

    static bool SourcesUnchanged(KnowledgeSearchResult result, CancellationToken token)
    {
        try
        {
            VaultPaths.ValidateRoot(result.VaultPath);
            foreach (var source in result.Sources.DistinctBy(s => s.RelativePath))
            {
                token.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(Path.Combine(result.VaultPath, source.RelativePath));
                var prefix = Path.EndsInDirectorySeparator(result.VaultPath) ? result.VaultPath : result.VaultPath + Path.DirectorySeparatorChar;
                if (!path.StartsWith(prefix, VaultPaths.Comparison)) return false;
                VaultPaths.ValidateRoot(Path.GetDirectoryName(path)!);
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
                if (ReadNote(result.VaultPath, path, token).Hash != source.ContentHash) return false;
            }
            return true;
        }
        catch (Exception e) when (e is KnowledgeBaseException or IOException or UnauthorizedAccessException or DecoderFallbackException) { return false; }
    }

    static Note ReadNote(string root, string path, CancellationToken token)
    {
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        if (length > MaxFileBytes) throw new IOException("Note exceeds the read limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var content = new StringBuilder();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (content.Length + read > MaxFileBytes) throw new IOException("Note exceeds the read limit.");
            content.Append(buffer, 0, read);
        }
        var after = new FileInfo(path);
        if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("Note changed during read.");
        var text = content.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        var lines = text.Split('\n');
        var metadata = "";
        if (lines.Length > 2 && lines[0] == "---")
        {
            var end = Array.FindIndex(lines, 1, Math.Min(60, lines.Length - 1), l => l == "---");
            if (end > 0) metadata = string.Join('\n', lines.Take(end + 1));
            if (metadata.Length > 1000) metadata = metadata[..1000];
        }
        var chunks = new List<Chunk>();
        var heading = Path.GetFileNameWithoutExtension(path);
        var body = new StringBuilder();
        var start = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var isHeading = lines[i].StartsWith('#') && lines[i].TrimStart('#').StartsWith(' ');
            if (body.Length > 0 && (isHeading || body.Length + lines[i].Length > ChunkCharacters)) Flush(i);
            if (isHeading)
            {
                heading = lines[i].TrimStart('#', ' ');
                if (heading.Length > 200) heading = heading[..200];
            }
            if (body.Length == 0) start = i + 1;
            // Long Markdown table/paragraph lines remain bounded and keep their actual line number.
            for (var offset = 0; offset < lines[i].Length; offset += ChunkCharacters)
            {
                if (body.Length == 0) start = i + 1;
                body.Append(lines[i].AsSpan(offset, Math.Min(ChunkCharacters, lines[i].Length - offset)));
                if (offset + ChunkCharacters < lines[i].Length) Flush(i + 1);
            }
            body.Append('\n');
        }
        Flush(lines.Length);
        return new(length, modified, text.Length, hash, chunks);

        void Flush(int end)
        {
            var value = body.ToString().TrimEnd('\n');
            body.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;
            if (chunks.Count >= MaxChunks) throw new IOException("Note exceeds the chunk limit.");
            var terms = Terms(value + " " + relative + " " + heading + " " + heading, false)
                .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            chunks.Add(new(new("", relative, heading, start, end, value, metadata, modified, hash), terms, terms.Values.Sum()));
        }
    }

    static IEnumerable<string> Terms(string value, bool query)
    {
        if (query) foreach (var noise in Noise) value = value.Replace(noise, " ", StringComparison.Ordinal);
        foreach (Match match in Words.Matches(value.ToLowerInvariant()))
        {
            var word = match.Value;
            if (word[0] is >= '\u3400' and <= '\u9fff')
            {
                if (word.Length == 1) continue;
                for (var i = 0; i < word.Length - 1; i++) yield return word.Substring(i, 2);
            }
            else if (word.Length > 1 && word is not ("the" or "is" or "of" or "and" or "how" or "what" or "to")) yield return word;
        }
    }

    sealed record Note(long Length, DateTime ModifiedUtc, int Characters, string Hash, IReadOnlyList<Chunk> Chunks);
    sealed record Chunk(KnowledgeSource Source, Dictionary<string, int> Terms, int WordCount);
}
