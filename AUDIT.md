# Digital Butler Audit

Date: 2026-04-25

Scope: quick code audit of the main `DigitalButler.*` app, with emphasis on flaws, weak spots, deficiencies, and likely bugs. This app is intended for one trusted user, so the highest priority is not multi-tenant hardening; it is correctness, reliability, data freshness, secret safety, and avoiding quiet bad answers.

Verification performed:

- `dotnet build DigitalButler.sln --no-restore` failed with no compiler errors.
- `dotnet build DigitalButler.Web/DigitalButler.Web.csproj --no-restore -v:normal` failed during package asset resolution because NuGet audit warnings for `MailKit` and `MimeKit` are treated as build-failing warnings.

## Critical

1. Build is currently blocked by dependency vulnerability warnings.
   - Files: `DigitalButler.Context/DigitalButler.Context.csproj`, transitive to `DigitalButler.Web`.
   - Symptoms: `NU1902` warnings for `MailKit 4.10.0` and `MimeKit 4.10.0`; build exits failed with `0 Error(s)`.
   - Impact: CI/deploy can fail even when the C# code compiles.
   - Suggested fix: update MailKit/MimeKit or explicitly configure NuGet audit warning policy.

2. Admin login accepts blank credentials if auth env/config is missing.
   - File: `DigitalButler.Web/Program.cs:285`.
   - Current behavior: `expectedUser`, `expectedPass`, and `expectedHash` can all be null/empty. A POST with blank username and password can satisfy `username == expectedUser && password == expectedPass`.
   - Impact: if the app is reachable and auth env vars are absent, the admin UI may be open.
   - Suggested fix: fail startup unless either `Auth:PasswordHash` or `Auth:Password` plus `Auth:Username` is configured; prefer password hash only, use constant-time comparison.

3. Vault indexing can silently drop chunks from large notes.
   - Files: `DigitalButler.Skills/VaultSearch/VaultIndexer.cs:240`, `DigitalButler.Data/Repositories/VaultSearchRepository.cs:131`.
   - Current behavior: indexing embeds all chunks in batches, then calls `ReplaceChunksForNoteAsync` per note group inside each batch. If one note has chunks in more than one embedding batch, later batches delete chunks saved by earlier batches.
   - Impact: large notes become partially searchable, with no obvious error.
   - Suggested fix: generate embeddings for all chunks first, then call `ReplaceChunksForNoteAsync` exactly once per note with the full chunk set.

4. Vault index can mark a note as up-to-date before its chunks/embeddings are safely written.
   - File: `DigitalButler.Skills/VaultSearch/VaultIndexer.cs:203`.
   - Current behavior: `VaultNotes.ContentHash` is upserted before embedding generation and chunk replacement. If embedding fails, the note hash now matches the file, so later indexing may skip it.
   - Impact: failed or partial indexing can become permanent until the note content changes.
   - Suggested fix: write note hash and chunks in one success path, or store an indexing status/hash only after embeddings commit.

5. Deleted vault notes are not reliably removed from chunk/vector tables.
   - Files: `DigitalButler.Data/ButlerDb.cs:24`, `DigitalButler.Data/Repositories/VaultSearchRepository.cs:96`.
   - Current behavior: SQLite foreign keys are declared but `PRAGMA foreign_keys = ON` is never enabled per connection. `DeleteNoteAsync`/`DeleteNotesAsync` delete from `VaultNotes` only, relying on cascade that SQLite does not enforce by default.
   - Impact: orphaned chunks/vector rows accumulate; stats become misleading and vector storage grows stale.
   - Suggested fix: enable foreign keys on every connection and explicitly delete chunks/vector rows for removed notes.

## High

6. Telegram skill executors are registered only when both bot token and allowed user ID exist.
   - Files: `DigitalButler.Web/Program.cs:240`, `DigitalButler.Telegram/ServiceCollectionExtensions.cs:11`, `DigitalButler.Web/Scheduler.cs:213`.
   - Current behavior: `ISummarySkillExecutor` and other executors live inside `AddTelegramBot()`. The scheduler always resolves `ISummarySkillExecutor` when a summary schedule fires.
   - Impact: schedules can fail at runtime if Telegram config is partial, and non-bot code is unnecessarily coupled to bot registration.
   - Suggested fix: register skill executors unconditionally; register only handlers/hosted bot conditionally.

7. Scheduled sync interval handling is not a real cron scheduler and mishandles many intervals.
   - File: `DigitalButler.Web/Scheduler.cs:100`.
   - Current behavior: schedule due is `nowUtc.Minute % intervalMinutes == 0`. An interval of `120` will only match minute `0`, which means hourly, not every two hours. The UI labels the field as "Cron Expression", but only a narrow `*/N` pattern is partly interpreted.
   - Impact: context freshness is unpredictable; UI promises cron semantics the scheduler does not implement.
   - Suggested fix: store last-run timestamps and use a cron parser or rename the field to "interval minutes" and implement elapsed-time checks.

8. Scheduled summaries can be duplicated or missed around process restarts and long ticks.
   - File: `DigitalButler.Web/Scheduler.cs:129`.
   - Current behavior: summaries fire when current local hour/minute equals schedule. There is no persisted "sent for this date/time" marker.
   - Impact: restart within the matching minute can resend; downtime during the matching minute skips the summary.
   - Suggested fix: persist summary run records keyed by schedule/date and send missed-but-not-expired summaries intentionally.

9. Context ingestion does not remove stale source items.
   - Files: `DigitalButler.Context/ContextUpdater.cs:21`, `DigitalButler.Data/Repositories/ContextRepository.cs:197`.
   - Current behavior: fetched items are upserted by `ExternalId`; items absent from the latest source fetch are never deleted or marked inactive.
   - Impact: cancelled calendar events, read emails from unread-only Gmail sync, deleted Obsidian daily notes, and old generated context can remain and contaminate summaries.
   - Suggested fix: track per-source sync windows and mark missing items stale/inactive, or delete rows for external IDs no longer present inside the fetched window.

10. Google Calendar cancelled/deleted events can remain after they were previously ingested.
    - Files: `DigitalButler.Context/GoogleCalendarContextSource.cs:83`, `DigitalButler.Data/Repositories/ContextRepository.cs:197`.
    - Current behavior: cancelled events are skipped, but existing rows for those events are not removed.
    - Impact: summaries may include meetings that were cancelled.
    - Suggested fix: sync deletions/cancellations explicitly, or rebuild calendar rows for the sync window.

11. Gmail ingestion fetches only envelope metadata, not email body.
    - File: `DigitalButler.Context/GmailContextSource.cs:142`.
    - Current behavior: fetches `Envelope`, `InternalDate`, and `UniqueId`; body only contains account/from/subject/date/message-id.
    - Impact: summaries cannot know what an email actually says. This limits usefulness and can cause misleading "email context" summaries.
    - Suggested fix: fetch a safe text body snippet, with size limits and basic quoted-thread stripping.

12. Date queries use server/UTC dates instead of the configured Butler timezone.
    - Files: `DigitalButler.Telegram/Handlers/TextMessageHandler.cs:386`, `DigitalButler.Skills/VaultSearch/VaultSearchService.cs:58`.
    - Current behavior: handlers use `DateTimeOffset.UtcNow`; search service uses `DateTimeOffset.Now`.
    - Impact: near local midnight in `Asia/Tbilisi`, "today", "yesterday", "this week", and summary-enrichment windows can point at the wrong day.
    - Suggested fix: inject `TimeZoneService` into date translation paths and use the same local reference time everywhere.

13. Date-range summaries convert `DateOnly` to UTC midnight, not local midnight.
    - File: `DigitalButler.Telegram/Skills/SummarySkillExecutor.cs:60`.
    - Current behavior: `new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)`.
    - Impact: calendar/context items around local day boundaries can be excluded or included on the wrong day.
    - Suggested fix: convert local date boundaries through `TimeZoneInfo.ConvertTimeToUtc`.

14. Add-to-Obsidian is present in the app but not actually routed from Telegram text/photo.
    - Files: `DigitalButler.Skills/SkillRouting.cs:137`, `DigitalButler.Telegram/Handlers/TextMessageHandler.cs:428`, `DigitalButler.Telegram/Handlers/PhotoMessageHandler.cs:81`.
    - Current behavior: router examples explicitly classify "save this to obsidian" as `unknown`; unknown messages only receive "couldn't match"; photo messages with captions ignore the actual image bytes and no path captures media.
    - Impact: a core requirement says unmatched Telegram messages can be captured into Obsidian, but the main bot path does not offer/perform capture.
    - Suggested fix: add an `AddToObsidian` route and conversation state, wire text/voice/photo capture to `IObsidianCaptureService`, and preserve media bytes when captions are present.

15. Media AI settings are not validated before calls.
    - File: `DigitalButler.Skills/MediaProcessing.cs:60`.
    - Current behavior: transcription and image analysis resolve settings but do not check API key/model before sending requests. Image analysis also silently defaults to `gpt-5-mini`; transcription always uses `whisper-1`.
    - Impact: confusing runtime 401/400 failures and less predictable model/provider behavior.
    - Suggested fix: validate required settings up front and expose dedicated task settings in the UI.

16. AI settings UI does not expose several task names the code actually uses.
    - Files: `DigitalButler.Web/Components/Pages/AiSettings.razor:49`, `DigitalButler.Skills/MediaProcessing.cs:62`, `DigitalButler.Skills/VaultSearch/EmbeddingService.cs:45`, `DigitalButler.Skills/CalendarEvent/CalendarEventParser.cs:31`.
    - Missing examples: `embeddings`, `audio-transcription`, `image-analysis`, `calendar-event-parsing`, `ai-context`.
    - Impact: admin UI suggests per-task settings are complete, but key features still rely on env defaults or fail.
    - Suggested fix: make task names centralized constants and render all known tasks.

## Medium

17. API keys and OAuth tokens are stored in plaintext SQLite.
    - Files: `DigitalButler.Data/ButlerSchemaInitializer.cs`, `DigitalButler.Data/Repositories/AiTaskSettingRepository.cs`, `DigitalButler.Data/Repositories/GoogleOAuthTokenRepository.cs`.
    - Impact for single-user app: acceptable only if the DB and backups are private; risky if the Docker volume, backups, or logs are copied elsewhere.
    - Suggested fix: avoid DB-stored API keys when possible, encrypt secrets at rest with a local key, or document the operational risk.

18. The global AI settings page shows API keys in a plain text input.
    - File: `DigitalButler.Web/Components/Pages/AiSettings.razor:34`.
    - Impact: shoulder-surfing/browser autofill/history risk in the admin UI.
    - Suggested fix: use password inputs, masked display, and "leave blank to keep existing" semantics.

19. SQLite connections lack `busy_timeout`, WAL mode, and explicit foreign-key setup.
    - File: `DigitalButler.Data/ButlerDb.cs:24`.
    - Impact: scheduler, manual sync, Telegram handlers, and Blazor UI can perform concurrent writes. SQLite may produce intermittent `database is locked` errors.
    - Suggested fix: set `PRAGMA busy_timeout`, enable WAL, and enable foreign keys when opening connections.

20. Schema migrations swallow all errors.
    - File: `DigitalButler.Data/ButlerSchemaInitializer.cs`.
    - Current behavior: every `ALTER TABLE` migration catches all exceptions and ignores them.
    - Impact: real migration failures are indistinguishable from "column already exists".
    - Suggested fix: ignore only known duplicate-column errors; log or fail on other migration errors.

21. Context update logs are inaccurate and duplicated for Obsidian.
    - Files: `DigitalButler.Web/Scheduler.cs:263`, `DigitalButler.Context/ObsidianDailyNotesContextSource.cs:32`.
    - Current behavior: generic scheduler logs have zero item counts for most updaters because `IContextUpdater.UpdateAsync` returns no stats. Obsidian source also writes its own log, then scheduler writes another log for the same run.
    - Impact: sync history looks cleaner or noisier than reality, which makes debugging stale summaries harder.
    - Suggested fix: have updaters return a stats object and centralize logging.

22. Obsidian daily note sync uses server local date, not Butler timezone.
    - File: `DigitalButler.Context/ObsidianDailyNotesContextSource.cs:56`.
    - Impact: cutoff window can be wrong if server timezone differs from user timezone.
    - Suggested fix: use `TimeZoneService` for cutoff and all "today" calculations.

23. Obsidian daily note updates rely only on `FileModifiedAt > existing.FileModifiedAt`.
    - File: `DigitalButler.Data/Repositories/ObsidianDailyNotesRepository.cs:75`.
    - Impact: edits can be missed if mounted filesystem timestamps are coarse, copied, or clock-skewed.
    - Suggested fix: store a content hash, not only modified time.

24. Daily note discovery is non-recursive.
    - File: `DigitalButler.Context/ObsidianDailyNotesContextSource.cs:57`.
    - Impact: nested daily-note folders are ignored unless the pattern points at exactly one directory.
    - Suggested fix: support globbing over the vault, not `Directory.GetFiles` on one directory.

25. Vault embedding dimensions are hard-coded to 1536 while the embedding model is configurable.
    - Files: `DigitalButler.Data/ButlerSchemaInitializer.cs`, `DigitalButler.Common/VaultSearchModels.cs`.
    - Impact: changing `EMBEDDING_MODEL` to a model with different dimensions breaks or corrupts vector search.
    - Suggested fix: validate model dimension or store dimension in schema/settings and rebuild the vector table when it changes.

26. Vault indexing has no cross-trigger lock.
    - Files: `DigitalButler.Web/Scheduler.cs:114`, `DigitalButler.Web/ManualSyncRunner.cs:15`, `DigitalButler.Web/Components/Pages/Context/ObsidianVault.razor:232`.
    - Current behavior: scheduled indexing, manual `/sync`, and UI indexing can run concurrently.
    - Impact: chunk replacement and note hash writes can race.
    - Suggested fix: share a singleton/semaphore around `IVaultIndexer`.

27. Home page manual summaries bypass the real summary executor.
    - File: `DigitalButler.Web/Components/Pages/Home.razor`.
    - Impact: web UI daily/weekly summary behavior can differ from Telegram/scheduler behavior, missing skill instructions, vault enrichment, and Obsidian analysis.
    - Suggested fix: use `ISummarySkillExecutor` everywhere.

28. The app mixes stable .NET 10 with older preview package versions.
    - Files: multiple `.csproj`.
    - Examples: `Microsoft.Extensions.* 10.0.0-preview.3`, `Microsoft.Data.Sqlite 9.0.0-preview.3`.
    - Impact: avoidable compatibility and security risk.
    - Suggested fix: upgrade to stable packages aligned with the SDK/runtime.

## Low / Product Gaps

29. No automated test project exists.
    - Impact: high-risk areas like schedule timing, date translation, vault indexing, and parser behavior are unprotected.
    - Suggested first tests: `DateQueryTranslator`, `TimeWindowHelper`, `VaultIndexer` multi-batch notes, `ContextRepository` stale-item handling, and Telegram routing for add-to-Obsidian.

30. Admin UI still uses broad destructive actions with limited guardrails.
    - File: `DigitalButler.Web/Components/Pages/Context/AllContext.razor`.
    - Impact: as a single user this is usable, but "delete all from source" is easy to misclick.
    - Suggested fix: require typing the source name for source-wide deletes or add undo/soft delete.

31. Login/session handling is minimal.
    - File: `DigitalButler.Web/Program.cs:285`.
    - Impact: no visible lockout, no configured cookie lifetime, no password rotation UI.
    - Suggested fix: still simple, but fail closed and set explicit cookie options.

32. Docker compose defaults are development-oriented.
    - File: `docker-compose.yml`.
    - Examples: `ASPNETCORE_ENVIRONMENT=Development`, hard-coded local notes mount.
    - Impact: fine locally, risky if reused for deployment.
    - Suggested fix: separate `docker-compose.local.yml` from production/deploy compose.

33. Error messages can expose implementation details to Telegram.
    - Files: Telegram handlers and `TelegramErrorNotifier`.
    - Impact: acceptable for one trusted user, but noisy and may leak raw exception text/API details into chat.
    - Suggested fix: keep detailed logs server-side and send concise user-facing failures.

## Suggested Fix Order

1. Restore/build health: dependency audit warnings, package upgrades.
2. Fail-closed admin auth.
3. Vault index correctness: multi-batch replacement, hash-after-success, deletion cleanup, foreign keys.
4. Scheduler correctness: real interval/cron handling and sent-run records.
5. Stale context cleanup for calendar/Gmail/Obsidian.
6. Telegram add-to-Obsidian capture path.
7. Timezone consistency for all date translation and date windows.
8. Tests for the above before further feature work.
