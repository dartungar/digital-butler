using System.Buffers.Binary;
using Dapper;
using DigitalButler.Common;

namespace DigitalButler.Data.Repositories;

public sealed class VaultSearchRepository
{
    private readonly IButlerDb _db;

    public VaultSearchRepository(IButlerDb db)
    {
        _db = db;
    }

    #region VaultNotes

    public async Task<VaultNote?> GetNoteByPathAsync(string filePath, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, FilePath, Title, ContentHash, FileModifiedAt, CreatedAt, UpdatedAt
            FROM VaultNotes
            WHERE FilePath = @FilePath;
            """;

        var row = await conn.QuerySingleOrDefaultAsync<VaultNoteRow>(sql, new { FilePath = filePath });
        return row is null ? null : MapNote(row);
    }

    public async Task<VaultNote?> GetNoteByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, FilePath, Title, ContentHash, FileModifiedAt, CreatedAt, UpdatedAt
            FROM VaultNotes
            WHERE Id = @Id;
            """;

        var row = await conn.QuerySingleOrDefaultAsync<VaultNoteRow>(sql, new { Id = id });
        return row is null ? null : MapNote(row);
    }

    public async Task<List<VaultNote>> GetAllNotesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, FilePath, Title, ContentHash, FileModifiedAt, CreatedAt, UpdatedAt
            FROM VaultNotes
            ORDER BY FilePath;
            """;

        var rows = await conn.QueryAsync<VaultNoteRow>(sql);
        return rows.Select(MapNote).ToList();
    }

    public async Task<Dictionary<string, (Guid Id, string ContentHash)>> GetNoteHashesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, FilePath, ContentHash
            FROM VaultNotes;
            """;

        var rows = await conn.QueryAsync<(Guid Id, string FilePath, string ContentHash)>(sql);
        return rows.ToDictionary(r => r.FilePath, r => (r.Id, r.ContentHash));
    }

    public async Task<Guid> UpsertNoteAsync(VaultNote note, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            INSERT INTO VaultNotes (Id, FilePath, Title, ContentHash, FileModifiedAt, CreatedAt, UpdatedAt)
            VALUES (@Id, @FilePath, @Title, @ContentHash, @FileModifiedAt, @CreatedAt, @UpdatedAt)
            ON CONFLICT(FilePath) DO UPDATE SET
                Title = excluded.Title,
                ContentHash = excluded.ContentHash,
                FileModifiedAt = excluded.FileModifiedAt,
                UpdatedAt = excluded.UpdatedAt
            RETURNING Id;
            """;

        return await conn.ExecuteScalarAsync<Guid>(sql, new
        {
            note.Id,
            note.FilePath,
            note.Title,
            note.ContentHash,
            note.FileModifiedAt,
            note.CreatedAt,
            note.UpdatedAt
        });
    }

    public async Task<int> DeleteNoteAsync(string filePath, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "DELETE FROM VaultNotes WHERE FilePath = @FilePath;";
        return await conn.ExecuteAsync(sql, new { FilePath = filePath });
    }

    public async Task<int> DeleteNotesAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        var list = filePaths.ToList();
        if (list.Count == 0) return 0;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = "DELETE FROM VaultNotes WHERE FilePath IN @FilePaths;";
        return await conn.ExecuteAsync(sql, new { FilePaths = list });
    }

    #endregion

    #region NoteChunks

    public async Task<List<NoteChunk>> GetChunksForNoteAsync(Guid noteId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, NoteId, ChunkIndex, ChunkText, StartLine, EndLine, CreatedAt
            FROM NoteChunks
            WHERE NoteId = @NoteId
            ORDER BY ChunkIndex;
            """;

        var rows = await conn.QueryAsync<NoteChunkRow>(sql, new { NoteId = noteId });
        return rows.Select(MapChunk).ToList();
    }

    public async Task ReplaceChunksForNoteAsync(Guid noteId, IEnumerable<NoteChunk> chunks, CancellationToken ct = default)
    {
        var chunkList = chunks.ToList();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Delete existing chunks and their embeddings
        var existingChunkIds = await conn.QueryAsync<Guid>(
            "SELECT Id FROM NoteChunks WHERE NoteId = @NoteId;",
            new { NoteId = noteId },
            tx);

        var idList = existingChunkIds.ToList();
        if (idList.Count > 0)
        {
            // Delete from vector table first (foreign key-like relationship)
            await conn.ExecuteAsync(
                "DELETE FROM vec_note_chunks WHERE chunk_id IN @Ids;",
                new { Ids = idList.Select(id => id.ToString()).ToList() },
                tx);

            await conn.ExecuteAsync(
                "DELETE FROM NoteChunks WHERE NoteId = @NoteId;",
                new { NoteId = noteId },
                tx);
        }

        // Insert new chunks
        foreach (var chunk in chunkList)
        {
            const string insertChunkSql = """
                INSERT INTO NoteChunks (Id, NoteId, ChunkIndex, ChunkText, StartLine, EndLine, CreatedAt)
                VALUES (@Id, @NoteId, @ChunkIndex, @ChunkText, @StartLine, @EndLine, @CreatedAt);
                """;

            await conn.ExecuteAsync(insertChunkSql, new
            {
                chunk.Id,
                chunk.NoteId,
                chunk.ChunkIndex,
                chunk.ChunkText,
                chunk.StartLine,
                chunk.EndLine,
                chunk.CreatedAt
            }, tx);

            // Insert embedding into vector table if available
            if (chunk.Embedding is { Length: > 0 })
            {
                const string insertVecSql = """
                    INSERT INTO vec_note_chunks (chunk_id, embedding)
                    VALUES (@ChunkId, @Embedding);
                    """;

                await conn.ExecuteAsync(insertVecSql, new
                {
                    ChunkId = chunk.Id.ToString(),
                    Embedding = FloatsToBlob(chunk.Embedding)
                }, tx);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task DeleteChunksForNoteAsync(Guid noteId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Get chunk IDs first
        var chunkIds = await conn.QueryAsync<Guid>(
            "SELECT Id FROM NoteChunks WHERE NoteId = @NoteId;",
            new { NoteId = noteId },
            tx);

        var idList = chunkIds.ToList();
        if (idList.Count > 0)
        {
            // Delete from vector table
            await conn.ExecuteAsync(
                "DELETE FROM vec_note_chunks WHERE chunk_id IN @Ids;",
                new { Ids = idList.Select(id => id.ToString()).ToList() },
                tx);
        }

        // Delete chunks
        await conn.ExecuteAsync(
            "DELETE FROM NoteChunks WHERE NoteId = @NoteId;",
            new { NoteId = noteId },
            tx);

        await tx.CommitAsync(ct);
    }

    #endregion

    #region Vector Search

    public async Task<List<VaultSearchResult>> SearchAsync(
        float[] queryEmbedding,
        int topK = 5,
        float minScore = 0.3f,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        // sqlite-vec uses distance (lower is better), we need to convert to similarity score
        // cosine distance ranges from 0 (identical) to 2 (opposite)
        // We'll convert: score = 1 - (distance / 2), so score ranges from 0 to 1
        // minScore of 0.3 means max distance of 1.4
        var maxDistance = 2.0f * (1.0f - minScore);

        // sqlite-vec requires the query embedding as JSON array string for MATCH
        var embeddingJson = "[" + string.Join(",", queryEmbedding.Select(f => f.ToString("G9", System.Globalization.CultureInfo.InvariantCulture))) + "]";
        var effectiveK = topK * 2; // Fetch more to allow filtering by distance

        // Note: sqlite-vec MATCH returns results directly, we need to join after
        // Using LOWER() for case-insensitive GUID comparison (SQLite stores uppercase, we insert lowercase)
        const string sql = """
            SELECT
                v.chunk_id,
                v.distance,
                c.ChunkText,
                c.ChunkIndex,
                c.StartLine,
                n.FilePath,
                n.Title
            FROM vec_note_chunks v
            INNER JOIN NoteChunks c ON LOWER(CAST(c.Id AS TEXT)) = LOWER(v.chunk_id)
            INNER JOIN VaultNotes n ON n.Id = c.NoteId
            WHERE v.embedding MATCH @QueryEmbedding
              AND k = @TopK
            ORDER BY v.distance ASC;
            """;

        var rows = await conn.QueryAsync<VaultSearchResultRow>(sql, new
        {
            QueryEmbedding = embeddingJson,
            TopK = effectiveK
        });

        return rows
            .Where(r => r.distance <= maxDistance)
            .Take(topK)
            .Select(r => new VaultSearchResult
            {
                FilePath = r.FilePath,
                Title = r.Title,
                ChunkText = r.ChunkText,
                ChunkIndex = r.ChunkIndex,
                StartLine = r.StartLine,
                Score = 1.0f - (r.distance / 2.0f)
            })
            .ToList();
    }

    public async Task<bool> IsVecAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vec_note_chunks LIMIT 1;");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> GetIndexedChunkCountAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM NoteChunks;");
    }

    public async Task<int> GetVecChunkCountAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vec_note_chunks;");
        }
        catch
        {
            return -1; // Indicates error
        }
    }

    /// <summary>
    /// Debug method to test raw vector search without joins
    /// </summary>
    public async Task<string> DebugSearchAsync(float[] queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await using var conn = await _db.OpenAsync(ct);

        try
        {
            // Check vec_note_chunks count
            var vecCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vec_note_chunks;");
            sb.AppendLine($"vec_note_chunks count: {vecCount}");

            // Check NoteChunks count
            var chunkCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM NoteChunks;");
            sb.AppendLine($"NoteChunks count: {chunkCount}");

            // Try a simple MATCH query without joins
            var embeddingJson = "[" + string.Join(",", queryEmbedding.Select(f => f.ToString("G9", System.Globalization.CultureInfo.InvariantCulture))) + "]";
            sb.AppendLine($"Embedding JSON length: {embeddingJson.Length}, first 100 chars: {embeddingJson[..Math.Min(100, embeddingJson.Length)]}...");

            // Raw vec query
            const string rawSql = """
                SELECT chunk_id, distance
                FROM vec_note_chunks
                WHERE embedding MATCH @QueryEmbedding
                  AND k = @TopK;
                """;

            var rawResults = await conn.QueryAsync<(string chunk_id, float distance)>(rawSql, new
            {
                QueryEmbedding = embeddingJson,
                TopK = topK
            });

            var rawList = rawResults.ToList();
            sb.AppendLine($"Raw MATCH results: {rawList.Count}");

            foreach (var r in rawList.Take(3))
            {
                sb.AppendLine($"  chunk_id={r.chunk_id}, distance={r.distance}");
            }

            // Check if chunk_ids match NoteChunks
            if (rawList.Count > 0)
            {
                var firstChunkId = rawList[0].chunk_id;
                sb.AppendLine($"First chunk_id from vec: {firstChunkId}");

                // Check what format NoteChunks.Id is stored as
                var sampleChunk = await conn.QueryFirstOrDefaultAsync<(string Id, string ChunkText)>(
                    "SELECT CAST(Id AS TEXT) as Id, substr(ChunkText, 1, 50) as ChunkText FROM NoteChunks LIMIT 1;");
                sb.AppendLine($"Sample NoteChunks.Id (as text): {sampleChunk.Id}");
                sb.AppendLine($"Sample chunk text: {sampleChunk.ChunkText}...");

                // Try matching
                var matchCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM NoteChunks WHERE CAST(Id AS TEXT) = @ChunkId;",
                    new { ChunkId = firstChunkId });
                sb.AppendLine($"NoteChunks matching first vec chunk_id: {matchCount}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
        }

        return sb.ToString();
    }

    public async Task<int> GetIndexedNoteCountAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM VaultNotes;");
    }

    #endregion

    #region Helpers

    private static byte[] FloatsToBlob(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        for (int i = 0; i < floats.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), floats[i]);
        }
        return bytes;
    }

    private static float[] BlobToFloats(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float)));
        }
        return floats;
    }

    private sealed class VaultNoteRow
    {
        public Guid Id { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public DateTimeOffset FileModifiedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class NoteChunkRow
    {
        public Guid Id { get; set; }
        public Guid NoteId { get; set; }
        public int ChunkIndex { get; set; }
        public string ChunkText { get; set; } = string.Empty;
        public int? StartLine { get; set; }
        public int? EndLine { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class VaultSearchResultRow
    {
        public string chunk_id { get; set; } = string.Empty;
        public float distance { get; set; }
        public string ChunkText { get; set; } = string.Empty;
        public int ChunkIndex { get; set; }
        public int? StartLine { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? Title { get; set; }
    }

    private static VaultNote MapNote(VaultNoteRow row) => new()
    {
        Id = row.Id,
        FilePath = row.FilePath,
        Title = row.Title,
        ContentHash = row.ContentHash,
        FileModifiedAt = row.FileModifiedAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    private static NoteChunk MapChunk(NoteChunkRow row) => new()
    {
        Id = row.Id,
        NoteId = row.NoteId,
        ChunkIndex = row.ChunkIndex,
        ChunkText = row.ChunkText,
        StartLine = row.StartLine,
        EndLine = row.EndLine,
        CreatedAt = row.CreatedAt
    };

    #endregion
}
