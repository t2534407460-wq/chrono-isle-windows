namespace ChronoIsle.App.Services.Knowledge;

public sealed record KnowledgeQuery(string Question);
public sealed record KnowledgeVerification(string SnapshotId, IReadOnlyList<SourceVersion> Sources);
public sealed record SourceVersion(string RelativePath, string ContentHash);
public sealed record VerificationResult(bool Unchanged);
public sealed record VaultFile(string Path, long Length, string Hash, DateTime ModifiedUtc);
public sealed record VaultManifest(string? BaseSnapshotId, IReadOnlyList<VaultFile> Files);
public sealed record PublishedVault(string SnapshotId, IReadOnlyList<VaultFile> Files);
