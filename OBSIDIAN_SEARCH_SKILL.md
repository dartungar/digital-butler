# Obsidian Vault Search Skill - Implementation Plan

## Overview

Add semantic search capability across the entire Obsidian vault (~7000+ notes, ~300MB). Users can search for relevant notes via natural language queries through Telegram.

## Architecture Decision: Hybrid Approach

Given the vault size (7000+ notes), pure in-memory search is borderline. Recommended approach:

1. **sqlite-vec extension** for vector search - native SQLite vector operations
2. **Fallback**: If sqlite-vec is problematic in deployment, use batched in-memory search with caching

### Why sqlite-vec?
- Single-file deployment (no external vector DB)
- Fits existing SQLite architecture
- Handles 100k+ vectors efficiently
- MIT licensed, actively maintained
- **Persistent storage** - no data loss on restart (unlike in-memory approach)

---

## Implementation Phases

### Phase 1: Data Layer

#### 1.1 New Table Schema

```sql
-- Store note metadata and content hash for change detection
CREATE TABLE VaultNotes (
    Id INTEGER PRIMARY KEY,
    FilePath TEXT NOT NULL UNIQUE,
    Title TEXT,
    ContentHash TEXT NOT NULL,
    LastModified TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

-- Store chunks with embeddings (separate for efficient updates)
CREATE TABLE NoteChunks (
    Id INTEGER PRIMARY KEY,
    NoteId INTEGER NOT NULL REFERENCES VaultNotes(Id) ON DELETE CASCADE,
    ChunkIndex INTEGER NOT NULL,
    ChunkText TEXT NOT NULL,
    StartLine INTEGER,
    EndLine INTEGER,
    Embedding BLOB NOT NULL,  -- 1536 floats for text-embedding-3-small
    UNIQUE(NoteId, ChunkIndex)
);

CREATE INDEX IX_NoteChunks_NoteId ON NoteChunks(NoteId);

-- Optional: sqlite-vec virtual table for fast similarity search
-- CREATE VIRTUAL TABLE vec_chunks USING vec0(
--     chunk_id INTEGER PRIMARY KEY,
--     embedding float[1536]
-- );
```

#### 1.2 New Repository

**File**: `DigitalButler.Data/Repositories/VaultSearchRepository.cs`

```csharp
public interface IVaultSearchRepository
{
    Task<VaultNote?> GetNoteByPathAsync(string filePath);
    Task<IEnumerable<VaultNote>> GetAllNotesAsync();
    Task UpsertNoteAsync(VaultNote note);
    Task DeleteNoteAsync(string filePath);

    Task<IEnumerable<NoteChunk>> GetChunksForNoteAsync(int noteId);
    Task ReplaceChunksForNoteAsync(int noteId, IEnumerable<NoteChunk> chunks);
    Task DeleteChunksForNoteAsync(int noteId);

    // For in-memory search fallback
    Task<IEnumerable<NoteChunkWithPath>> GetAllChunksAsync();

    // For sqlite-vec (if implemented)
    Task<IEnumerable<SearchResult>> VectorSearchAsync(float[] queryEmbedding, int topK);
}
```

#### 1.3 Models

**File**: `DigitalButler.Common/Models/VaultSearch.cs`

```csharp
public record VaultNote(
    int Id,
    string FilePath,
    string? Title,
    string ContentHash,
    DateTime LastModified,
    DateTime UpdatedAt
);

public record NoteChunk(
    int Id,
    int NoteId,
    int ChunkIndex,
    string ChunkText,
    int? StartLine,
    int? EndLine,
    byte[] Embedding  // Serialized float[]
);

public record NoteChunkWithPath(
    int ChunkId,
    string FilePath,
    string? Title,
    int ChunkIndex,
    string ChunkText,
    byte[] Embedding
);

public record VaultSearchResult(
    string FilePath,
    string? Title,
    string ChunkText,
    float Score,
    int? StartLine
);
```

---

### Phase 2: Embedding Service

#### 2.1 Embedding Generation

**File**: `DigitalButler.Skills/Services/EmbeddingService.cs`

```csharp
public interface IEmbeddingService
{
    Task<float[]> GetEmbeddingAsync(string text);
    Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(IEnumerable<string> texts);
}

public class OpenAiEmbeddingService : IEmbeddingService
{
    // Use text-embedding-3-small (1536 dimensions)
    // ~$0.02 per 1M tokens
    // Batch requests (max 2048 inputs per request)
}
```

#### 2.2 Chunking Strategy

**File**: `DigitalButler.Skills/Services/NoteChunker.cs`

```csharp
public interface INoteChunker
{
    IEnumerable<ChunkInfo> ChunkNote(string content, string filePath);
}

public record ChunkInfo(
    string Text,
    int ChunkIndex,
    int StartLine,
    int EndLine
);
```

**Chunking rules**:
1. Target chunk size: ~500 tokens (~2000 chars)
2. Respect markdown structure:
   - Never split mid-paragraph
   - Prefer splitting at `## ` headers
   - Keep YAML frontmatter with first chunk
3. Overlap: 50-100 tokens between chunks for context continuity
4. Include note title/path as prefix for context:
   ```
   [Note: Projects/MyProject.md]

   ## Implementation Details
   The system uses...
   ```

---

### Phase 3: Indexing Service

#### 3.1 Vault Indexer

**File**: `DigitalButler.Context/Services/VaultIndexer.cs`

```csharp
public interface IVaultIndexer
{
    Task<IndexingResult> IndexVaultAsync(CancellationToken ct = default);
    Task<IndexingResult> IndexNoteAsync(string filePath, CancellationToken ct = default);
    Task RemoveNoteAsync(string filePath);
}

public record IndexingResult(
    int NotesProcessed,
    int NotesAdded,
    int NotesUpdated,
    int NotesRemoved,
    int ChunksCreated,
    TimeSpan Duration
);
```

**Indexing logic**:
1. Scan `OBSIDIAN_VAULT_PATH` for all `.md` files
2. For each file:
   - Compute SHA256 hash of content
   - Compare with stored `ContentHash`
   - If changed or new:
     - Parse content, extract title from frontmatter or filename
     - Chunk the content
     - Generate embeddings (batch API calls)
     - Store/update in database
3. Remove entries for deleted files
4. Log progress to `ContextUpdateLog`

#### 3.2 Exclusion Patterns

Support excluding certain paths (templates, daily notes if already handled, etc.):

```csharp
public class VaultIndexerOptions
{
    public string VaultPath { get; set; }
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "**/templates/**",
        "**/.obsidian/**"
        // Daily notes ARE indexed - useful for "what did I do yesterday?" queries
    };
    public int BatchSize { get; set; } = 100;  // Embedding API batch size
}
```

---

### Phase 4: Search Service

#### 4.1 Vector Search

**File**: `DigitalButler.Skills/Services/VaultSearchService.cs`

```csharp
public interface IVaultSearchService
{
    Task<IReadOnlyList<VaultSearchResult>> SearchAsync(
        string query,
        int topK = 5,
        float minScore = 0.7f);
}
```

**Search implementation**:
1. Generate embedding for query
2. Search for similar chunks:
   - **sqlite-vec**: Use virtual table query
   - **In-memory fallback**: Load all embeddings, compute cosine similarity
3. Deduplicate results from same note (keep highest score)
4. Return top K results with metadata

#### 4.2 Cosine Similarity (for fallback)

```csharp
public static float CosineSimilarity(float[] a, float[] b)
{
    float dot = 0, normA = 0, normB = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        normA += a[i] * a[i];
        normB += b[i] * b[i];
    }
    return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
}
```

#### 4.3 Caching Strategy

For in-memory search:
- Cache all embeddings on first search
- Invalidate cache after indexing runs
- Memory estimate: 7000 notes × 3 chunks avg × 1536 floats × 4 bytes = ~125MB

---

### Phase 5: Skill Integration

#### 5.1 New Skill Type

**File**: `DigitalButler.Common/Enums/SkillType.cs`

```csharp
public enum SkillType
{
    // existing...
    Summary,
    Motivation,
    Activities,
    DrawingReference,
    CalendarEvent,

    // new
    VaultSearch
}
```

#### 5.2 Skill Router Update

**File**: `DigitalButler.Skills/SkillRouting.cs`

Update the routing prompt to recognize search intent AND determine if other skills need vault enrichment:

```
VaultSearch - User wants to find information in their notes/vault. Examples:
- "What did I write about project X?"
- "Find my notes on machine learning"
- "Search for meeting notes with John"
- "Do I have anything about home renovation?"
```

**New: Vault Enrichment Flag**

The skill router should return an additional flag indicating whether the routed skill would benefit from vault search context:

```csharp
public record SkillRoutingResult(
    SkillType Skill,
    bool NeedsVaultEnrichment,  // Should we search vault for relevant context?
    string? VaultSearchQuery    // Query to use for enrichment (may differ from user message)
);
```

Examples:
- "Summarize my day" → Skill: Summary, NeedsVaultEnrichment: true, VaultSearchQuery: "2026-01-18 daily note"
- "What meetings do I have?" → Skill: Summary, NeedsVaultEnrichment: false
- "Help me with my home renovation project" → Skill: General, NeedsVaultEnrichment: true, VaultSearchQuery: "home renovation project"

#### 5.2.1 Date Translation

**File**: `DigitalButler.Skills/Services/DateQueryTranslator.cs`

Translates natural language date references to concrete dates/patterns for search:

```csharp
public interface IDateQueryTranslator
{
    string TranslateQuery(string query, DateTime referenceDate);
}
```

Translation examples (assuming today is 2026-01-19):
- "yesterday" → "2026-01-18"
- "last week" → "2026-W03" or "2026-01-12 to 2026-01-18"
- "last Monday" → "2026-01-13"
- "this month" → "2026-01" or "January 2026"
- "in December" → "2025-12" (assumes most recent December)
- "two weeks ago" → "2026-01-05"

**Smart date handling**:
- Preserve original query alongside translated version for hybrid search
- Consider both ISO formats (2026-01-18) and natural formats (January 18, 2026)
- Daily notes often use format like "2026-01-18.md" - include filename patterns
- Weekly summaries may use "2026-W03" format

#### 5.3 Skill Executor

**File**: `DigitalButler.Telegram/SkillExecutors/VaultSearchSkillExecutor.cs`

```csharp
public class VaultSearchSkillExecutor : ISkillExecutor
{
    public async Task<string> ExecuteAsync(string userMessage, ...)
    {
        // 1. Extract search query (may need AI to refine)
        // 2. Call IVaultSearchService.SearchAsync()
        // 3. Format results for Telegram:
        //    - Note title/path
        //    - Relevant excerpt
        //    - Match score (optional)
        // 4. If no results, suggest alternatives
    }
}
```

#### 5.4 Explicit Command

**File**: `DigitalButler.Telegram/Handlers/TextMessageHandler.cs`

Add `/search <query>` command for explicit searches:

```csharp
case "/search":
    var query = messageText.Substring(7).Trim();
    if (string.IsNullOrEmpty(query))
    {
        await SendAsync("Usage: /search <query>");
        return;
    }
    await _vaultSearchExecutor.ExecuteAsync(query, ...);
    break;
```

---

### Phase 6: Background Indexing

#### 6.1 Scheduled Indexing

**File**: `DigitalButler.Web/Scheduler.cs`

Add vault indexing to the scheduler:

```csharp
// Run indexing after vault sync (detect via file watcher or schedule)
// Suggested: Every 30 minutes or on /sync command

private async Task RunVaultIndexingAsync()
{
    var result = await _vaultIndexer.IndexVaultAsync();
    _logger.LogInformation(
        "Vault indexing complete: {Processed} notes, {Added} added, {Updated} updated",
        result.NotesProcessed, result.NotesAdded, result.NotesUpdated);
}
```

#### 6.2 Manual Trigger

Extend `/sync` command to include vault indexing:

```csharp
case "/sync":
    await SendAsync("Syncing context sources...");
    await _contextService.UpdateAllAsync();

    await SendAsync("Indexing vault for search...");
    var result = await _vaultIndexer.IndexVaultAsync();
    await SendAsync($"Indexed {result.NotesProcessed} notes ({result.NotesAdded} new, {result.NotesUpdated} updated)");
    break;
```

---

## Configuration

### Environment Variables

```bash
# Existing
OBSIDIAN_VAULT_PATH=/var/notes

# New
OBSIDIAN_SEARCH_ENABLED=true
OBSIDIAN_SEARCH_EXCLUDE_PATTERNS=**/templates/**,**/.obsidian/**
OBSIDIAN_SEARCH_CHUNK_SIZE=500        # tokens
OBSIDIAN_SEARCH_CHUNK_OVERLAP=50      # tokens
OBSIDIAN_SEARCH_MIN_SCORE=0.7         # similarity threshold
OBSIDIAN_SEARCH_TOP_K=5               # max results
EMBEDDING_MODEL=text-embedding-3-small
```

### App Settings (Database)

Allow runtime configuration via `AppSettings` table:
- `VaultSearch:Enabled`
- `VaultSearch:MinScore`
- `VaultSearch:TopK`

---

## Cost Estimation

### Initial Indexing

- 7000 notes × ~1500 tokens avg = 10.5M tokens
- text-embedding-3-small: $0.02/1M tokens
- **Initial cost: ~$0.21**

### Incremental Updates

- Assuming 50 notes change daily × 1500 tokens = 75k tokens
- **Daily cost: ~$0.0015**

### Search Queries

- 1 embedding per query × 50 queries/day = ~5k tokens
- **Negligible cost**

---

## Implementation Order

1. **Data Layer** (1-2 hours)
   - [ ] Add schema to `ButlerSchemaInitializer`
   - [ ] Create `VaultSearchRepository`
   - [ ] Add models to Common

2. **Embedding Service** (1-2 hours)
   - [ ] Create `OpenAiEmbeddingService`
   - [ ] Add embedding serialization helpers

3. **Chunking Service** (1-2 hours)
   - [ ] Create `NoteChunker` with markdown-aware splitting
   - [ ] Add tests for edge cases

4. **Indexing Service** (2-3 hours)
   - [ ] Create `VaultIndexer`
   - [ ] Add exclusion pattern support
   - [ ] Integrate with scheduler

5. **Search Service** (1-2 hours)
   - [ ] Create `VaultSearchService` with sqlite-vec
   - [ ] Add date query translation

6. **Skill Integration** (1-2 hours)
   - [ ] Add `VaultSearch` to skill types
   - [ ] Update skill router prompt
   - [ ] Create `VaultSearchSkillExecutor`
   - [ ] Add `/search` command

7. **Testing & Tuning** (2-3 hours)
   - [ ] Test with real vault
   - [ ] Tune chunk size and overlap
   - [ ] Tune similarity threshold
   - [ ] Optimize for response time

---

## Future Enhancements

1. **Hybrid Search**: Combine vector search with keyword search (BM25) for better results
2. **Reranking**: Use a cross-encoder model to rerank top results
3. **Link Graph**: Use Obsidian's `[[links]]` to boost related notes
4. **Incremental Sync**: File watcher for real-time indexing
5. **Query Expansion**: Use AI to expand/refine search queries
6. **Result Summarization**: AI-generated summary of search results
7. **Context Integration**: Include search results as context for other skills

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| sqlite-vec deployment issues | High | Start with in-memory fallback, add sqlite-vec later |
| Memory usage with 7k+ notes | Medium | Lazy loading, pagination, or sqlite-vec |
| Embedding API rate limits | Low | Batch requests, exponential backoff |
| Poor search quality | Medium | Tune chunking, add reranking, hybrid search |
| Slow initial indexing | Low | Background task, progress reporting |

---

## Open Questions

1. Should search results include a direct link to open in Obsidian? (`obsidian://open?vault=...`)
2. Should we index file attachments (PDFs, images) in the future?
3. Should search be available as context for other skills (e.g., include relevant notes in daily summary)?
