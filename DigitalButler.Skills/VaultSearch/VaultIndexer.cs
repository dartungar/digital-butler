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
        var relativePath = GetRelativePath(filePath);
        await _repo.DeleteNoteAsync(relativePath, ct);
        _logger.LogDebug("Removed note from index: {Path}", relativePath);
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

        // Process files and collect all chunks
        var allChunksToEmbed = new List<(string filePath, NoteChunk chunk)>();

        foreach (var filePath in filePaths)
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                var relativePath = GetRelativePath(filePath);
                var title = ExtractTitle(content, filePath);
                var contentHash = ComputeHash(content);
                var fileModified = File.GetLastWriteTimeUtc(filePath);

                // Create/update the note record
                var note = new VaultNote
                {
                    FilePath = relativePath,
                    Title = title,
                    ContentHash = contentHash,
                    FileModifiedAt = fileModified,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                var noteId = await _repo.UpsertNoteAsync(note, ct);
                note.Id = noteId;

                // Chunk the content
                var chunks = _chunker.ChunkNote(content, relativePath, title).ToList();

                foreach (var chunkInfo in chunks)
                {
                    var chunk = new NoteChunk
                    {
                        NoteId = noteId,
                        ChunkIndex = chunkInfo.ChunkIndex,
                        ChunkText = chunkInfo.Text,
                        StartLine = chunkInfo.StartLine,
                        EndLine = chunkInfo.EndLine
                    };

                    allChunksToEmbed.Add((filePath, chunk));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing note {Path}", filePath);
                result.Errors.Add($"Error processing {filePath}: {ex.Message}");
            }
        }

        // Generate embeddings in batches
        var batchSize = _options.EmbeddingBatchSize;
        var batches = allChunksToEmbed.Chunk(batchSize).ToList();

        _logger.LogDebug("Generating embeddings for {Count} chunks in {Batches} batches", allChunksToEmbed.Count, batches.Count);

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var texts = batch.Select(b => b.chunk.ChunkText).ToList();
                var embeddings = await _embeddingService.GetEmbeddingsAsync(texts, ct);

                // Assign embeddings to chunks
                for (int i = 0; i < batch.Length; i++)
                {
                    batch[i].chunk.Embedding = embeddings[i];
                }

                // Group chunks by note and save
                var chunksByNote = batch.GroupBy(b => b.chunk.NoteId);
                foreach (var noteGroup in chunksByNote)
                {
                    var noteChunks = noteGroup.Select(g => g.chunk).OrderBy(c => c.ChunkIndex).ToList();
                    await _repo.ReplaceChunksForNoteAsync(noteGroup.Key, noteChunks, ct);
                    result.ChunksCreated += noteChunks.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embeddings for batch");
                result.Errors.Add($"Embedding error: {ex.Message}");
            }
        }
    }

    private async Task ProcessNoteAsync(string filePath, VaultIndexingResult result, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var relativePath = GetRelativePath(filePath);
        var title = ExtractTitle(content, filePath);
        var contentHash = ComputeHash(content);
        var fileModified = File.GetLastWriteTimeUtc(filePath);

        var note = new VaultNote
        {
            FilePath = relativePath,
            Title = title,
            ContentHash = contentHash,
            FileModifiedAt = fileModified,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var noteId = await _repo.UpsertNoteAsync(note, ct);

        var chunks = _chunker.ChunkNote(content, relativePath, title).ToList();
        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await _embeddingService.GetEmbeddingsAsync(texts, ct);

        var noteChunks = chunks.Select((c, i) => new NoteChunk
        {
            NoteId = noteId,
            ChunkIndex = c.ChunkIndex,
            ChunkText = c.Text,
            StartLine = c.StartLine,
            EndLine = c.EndLine,
            Embedding = embeddings[i]
        }).ToList();

        await _repo.ReplaceChunksForNoteAsync(noteId, noteChunks, ct);
        result.ChunksCreated = noteChunks.Count;
    }

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
