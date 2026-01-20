namespace DigitalButler.Common;

/// <summary>
/// Represents a note in the Obsidian vault for indexing purposes.
/// </summary>
public class VaultNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset FileModifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents a chunk of a note with its embedding for vector search.
/// </summary>
public class NoteChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NoteId { get; set; }
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The embedding vector for this chunk (1536 floats for text-embedding-3-small).
    /// Stored separately in vec_note_chunks virtual table.
    /// </summary>
    public float[]? Embedding { get; set; }
}

/// <summary>
/// Result of a vault search operation.
/// </summary>
public class VaultSearchResult
{
    public string FilePath { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public float Score { get; set; }
    public int? StartLine { get; set; }
    public int ChunkIndex { get; set; }
}

/// <summary>
/// Result of vault indexing operation.
/// </summary>
public class VaultIndexingResult
{
    public int NotesScanned { get; set; }
    public int NotesAdded { get; set; }
    public int NotesUpdated { get; set; }
    public int NotesRemoved { get; set; }
    public int ChunksCreated { get; set; }
    public int ChunksRemoved { get; set; }
    public TimeSpan Duration { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Information about a chunk of text from a note, before embedding.
/// </summary>
public class ChunkInfo
{
    public string Text { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

/// <summary>
/// Result from skill routing, including vault enrichment decision.
/// </summary>
public class SkillRoutingResult
{
    public ButlerSkill Skill { get; set; }

    /// <summary>
    /// Whether the skill execution should include relevant notes from vault search.
    /// </summary>
    public bool NeedsVaultEnrichment { get; set; }

    /// <summary>
    /// The query to use for vault search enrichment.
    /// May be transformed from the original user message (e.g., date translation).
    /// </summary>
    public string? VaultSearchQuery { get; set; }
}

/// <summary>
/// Configuration for vault indexing.
/// </summary>
public class VaultIndexerOptions
{
    public string VaultPath { get; set; } = "/var/notes";
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "**/templates/**",
        "**/.obsidian/**"
    };
    public int ChunkTargetTokens { get; set; } = 500;
    public int ChunkOverlapTokens { get; set; } = 50;
    public int EmbeddingBatchSize { get; set; } = 100;
}

/// <summary>
/// Configuration for vault search.
/// </summary>
public class VaultSearchOptions
{
    public bool Enabled { get; set; } = true;
    public float MinScore { get; set; } = 0.3f;
    public int TopK { get; set; } = 5;
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
}
