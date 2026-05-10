using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;

namespace DigitalButler.Skills.VaultSearch;

public interface IVaultIndexer
{
    Task<VaultIndexingResult> IndexVaultAsync(CancellationToken ct = default);
    Task<VaultIndexingResult> IndexNoteAsync(string filePath, CancellationToken ct = default);
    Task RemoveNoteAsync(string filePath, CancellationToken ct = default);
}

public partial class VaultIndexer : IVaultIndexer
{
    private static readonly SemaphoreSlim IndexLock = new(1, 1);

    private readonly VaultSearchRepository _repo;
    private readonly IEmbeddingService _embeddingService;
    private readonly INoteChunker _chunker;
    private readonly VaultIndexerOptions _options;
    private readonly ILogger<VaultIndexer> _logger;

    public VaultIndexer(
        VaultSearchRepository repo,
        IEmbeddingService embeddingService,
        INoteChunker chunker,
        IOptions<VaultIndexerOptions> options,
        ILogger<VaultIndexer> logger)
    {
        _repo = repo;
        _embeddingService = embeddingService;
        _chunker = chunker;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VaultIndexingResult> IndexVaultAsync(CancellationToken ct = default)
    {
        await IndexLock.WaitAsync(ct);
        try
        {
            return await IndexVaultCoreAsync(ct);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private async Task<VaultIndexingResult> IndexVaultCoreAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new VaultIndexingResult();

        if (!Directory.Exists(_options.VaultPath))
        {
            _logger.LogWarning("Vault path does not exist: {Path}", _options.VaultPath);
            result.Errors.Add($"Vault path does not exist: {_options.VaultPath}");
            result.Duration = sw.Elapsed;
            return result;
        }

        try
        {
            // Get all markdown files in vault
            var allFiles = GetVaultFiles();
            result.NotesScanned = allFiles.Count;

            _logger.LogInformation("Found {Count} markdown files in vault", allFiles.Count);

            // Get existing notes from database
            var existingNotes = await _repo.GetNoteHashesAsync(ct);

            // Determine what needs to be processed
            var toAdd = new List<string>();
            var toUpdate = new List<string>();
            var toRemove = existingNotes.Keys.ToHashSet();

            foreach (var filePath in allFiles)
            {
                var relativePath = GetRelativePath(filePath);
                toRemove.Remove(relativePath);

                var content = await File.ReadAllTextAsync(filePath, ct);
                var contentHash = ComputeHash(content);

                if (!existingNotes.TryGetValue(relativePath, out var existing))
                {
                    toAdd.Add(filePath);
                }
                else if (existing.ContentHash != contentHash)
                {
                    toUpdate.Add(filePath);
                }
            }

            _logger.LogInformation(
                "Indexing changes: {Add} to add, {Update} to update, {Remove} to remove",
                toAdd.Count, toUpdate.Count, toRemove.Count);

            // Process additions and updates in batches
            var toProcess = toAdd.Concat(toUpdate).ToList();
            await ProcessNotesInBatchesAsync(toProcess, result, ct);

            result.NotesAdded = toAdd.Count;
            result.NotesUpdated = toUpdate.Count;

            // Remove deleted files
            if (toRemove.Count > 0)
            {
                await _repo.DeleteNotesAsync(toRemove, ct);
                result.NotesRemoved = toRemove.Count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during vault indexing");
            result.Errors.Add($"Indexing error: {ex.Message}");
        }

        result.Duration = sw.Elapsed;

        _logger.LogInformation(
            "Vault indexing complete in {Duration:F1}s: {Added} added, {Updated} updated, {Removed} removed, {Chunks} chunks, {Errors} errors",
            result.Duration.TotalSeconds,
            result.NotesAdded,
            result.NotesUpdated,
            result.NotesRemoved,
            result.ChunksCreated,
            result.Errors.Count);

        return result;
    }

    public async Task<VaultIndexingResult> IndexNoteAsync(string filePath, CancellationToken ct = default)
    {
        await IndexLock.WaitAsync(ct);
        try
        {
            return await IndexNoteCoreAsync(filePath, ct);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private async Task<VaultIndexingResult> IndexNoteCoreAsync(string filePath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new VaultIndexingResult { NotesScanned = 1 };

        try
        {
            var fullPath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(_options.VaultPath, filePath);

            if (!File.Exists(fullPath))
            {
                result.Errors.Add($"File not found: {fullPath}");
                return result;
            }

            await ProcessNoteAsync(fullPath, result, ct);
            result.NotesAdded = 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing note {Path}", filePath);
            result.Errors.Add($"Error indexing {filePath}: {ex.Message}");
        }

        result.Duration = sw.Elapsed;
        return result;
    }

    public async Task RemoveNoteAsync(string filePath, CancellationToken ct = default)
    {
        await IndexLock.WaitAsync(ct);
        try
        {
            var relativePath = GetRelativePath(filePath);
            await _repo.DeleteNoteAsync(relativePath, ct);
            _logger.LogDebug("Removed note from index: {Path}", relativePath);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private List<string> GetVaultFiles()
    {
        var allMdFiles = Directory.GetFiles(_options.VaultPath, "*.md", SearchOption.AllDirectories);

        // Build exclusion matcher
        var matcher = new Matcher();
        matcher.AddInclude("**/*.md");
        foreach (var pattern in _options.ExcludePatterns)
        {
            matcher.AddExclude(pattern);
        }

        var matchResult = matcher.Match(_options.VaultPath, allMdFiles.Select(f => Path.GetRelativePath(_options.VaultPath, f)));

        return matchResult.Files
            .Select(f => Path.Combine(_options.VaultPath, f.Path))
            .ToList();
    }

    private async Task ProcessNotesInBatchesAsync(
        List<string> filePaths,
        VaultIndexingResult result,
        CancellationToken ct)
    {
        if (filePaths.Count == 0) return;

        var preparedNotes = new List<PreparedNote>();

        foreach (var filePath in filePaths)
        {
            try
            {
                preparedNotes.Add(await PrepareNoteAsync(filePath, ct));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing note {Path}", filePath);
                result.Errors.Add($"Error processing {filePath}: {ex.Message}");
            }
        }

        var allChunksToEmbed = preparedNotes
            .SelectMany(note => note.Chunks.Select(chunk => (note.FilePath, chunk)))
            .ToList();

        // Generate embeddings in batches
        var batchSize = Math.Max(1, _options.EmbeddingBatchSize);
        var batches = allChunksToEmbed.Chunk(batchSize).ToList();

        _logger.LogDebug("Generating embeddings for {Count} chunks in {Batches} batches", allChunksToEmbed.Count, batches.Count);

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var texts = batch.Select(b => b.chunk.ChunkText).ToList();
                var embeddings = await _embeddingService.GetEmbeddingsAsync(texts, ct);
                if (embeddings.Count != batch.Length)
                {
                    throw new InvalidOperationException($"Embedding service returned {embeddings.Count} embeddings for {batch.Length} chunks.");
                }

                // Assign embeddings to chunks
                for (int i = 0; i < batch.Length; i++)
                {
                    batch[i].chunk.Embedding = embeddings[i];
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embeddings for batch");
                result.Errors.Add($"Embedding error: {ex.Message}");
            }
        }

        foreach (var prepared in preparedNotes)
        {
            if (prepared.Chunks.Any(c => c.Embedding is not { Length: > 0 }))
            {
                _logger.LogWarning("Skipping note {Path} because embeddings were not generated for every chunk", prepared.RelativePath);
                result.Errors.Add($"Embedding incomplete for {prepared.RelativePath}; kept existing index data.");
                continue;
            }

            try
            {
                await _repo.SaveNoteWithChunksAsync(prepared.Note, prepared.Chunks, ct);
                result.ChunksCreated += prepared.Chunks.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error saving indexed note {Path}", prepared.RelativePath);
                result.Errors.Add($"Error saving {prepared.RelativePath}: {ex.Message}");
            }
        }
    }

    private async Task ProcessNoteAsync(string filePath, VaultIndexingResult result, CancellationToken ct)
    {
        var prepared = await PrepareNoteAsync(filePath, ct);
        var texts = prepared.Chunks.Select(c => c.ChunkText).ToList();
        if (texts.Count == 0)
        {
            await _repo.SaveNoteWithChunksAsync(prepared.Note, prepared.Chunks, ct);
            result.ChunksCreated = 0;
            return;
        }

        var embeddings = await _embeddingService.GetEmbeddingsAsync(texts, ct);
        if (embeddings.Count != prepared.Chunks.Count)
        {
            throw new InvalidOperationException($"Embedding service returned {embeddings.Count} embeddings for {prepared.Chunks.Count} chunks.");
        }

        for (var i = 0; i < prepared.Chunks.Count; i++)
        {
            prepared.Chunks[i].Embedding = embeddings[i];
        }

        await _repo.SaveNoteWithChunksAsync(prepared.Note, prepared.Chunks, ct);
        result.ChunksCreated = prepared.Chunks.Count;
    }

    private async Task<PreparedNote> PrepareNoteAsync(string filePath, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var relativePath = GetRelativePath(filePath);
        var title = ExtractTitle(content, filePath);
        var contentHash = ComputeHash(content);
        var fileModified = File.GetLastWriteTimeUtc(filePath);
        var existing = await _repo.GetNoteByPathAsync(relativePath, ct);

        var note = new VaultNote
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            FilePath = relativePath,
            Title = title,
            ContentHash = contentHash,
            FileModifiedAt = fileModified,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var chunks = _chunker.ChunkNote(content, relativePath, title)
            .Select(chunkInfo => new NoteChunk
            {
                NoteId = note.Id,
                ChunkIndex = chunkInfo.ChunkIndex,
                ChunkText = chunkInfo.Text,
                StartLine = chunkInfo.StartLine,
                EndLine = chunkInfo.EndLine
            })
            .ToList();

        return new PreparedNote(filePath, relativePath, note, chunks);
    }

    private sealed record PreparedNote(string FilePath, string RelativePath, VaultNote Note, List<NoteChunk> Chunks);

    private string GetRelativePath(string fullPath)
    {
        return Path.GetRelativePath(_options.VaultPath, fullPath).Replace('\\', '/');
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string? ExtractTitle(string content, string filePath)
    {
        // Try to extract title from frontmatter
        var titleMatch = TitleRegex().Match(content);
        if (titleMatch.Success)
        {
            return titleMatch.Groups[1].Value.Trim().Trim('"', '\'');
        }

        // Try to extract from first H1 header
        var h1Match = H1Regex().Match(content);
        if (h1Match.Success)
        {
            return h1Match.Groups[1].Value.Trim();
        }

        // Fall back to filename
        return Path.GetFileNameWithoutExtension(filePath);
    }

    [GeneratedRegex(@"(?:^|\n)title:\s*['""]?(.+?)['""]?\s*(?:\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex H1Regex();
}
