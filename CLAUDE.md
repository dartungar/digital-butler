# CLAUDE.md - Digital Butler

## Project Overview

Digital Butler is a personal AI assistant Telegram bot built with .NET 10.0.
It is used STRICTLY by its creator (it has only 1 user - me).
It aggregates information from multiple context sources (Google Calendar, Gmail, personal notes, Obsidian vault) and uses AI to provide daily summaries, motivational messages, personalized recommendations, and semantic search over notes.

## Solution Structure

```
DigitalButler.sln
├── DigitalButler.Common          # Shared domain models & interfaces
├── DigitalButler.Data            # SQLite database & repositories (Dapper)
├── DigitalButler.Context         # Context sources & updaters
├── DigitalButler.Skills          # AI services, skill routing, vault search
├── DigitalButler.Telegram        # Telegram bot implementation
└── DigitalButler.Web             # ASP.NET Core Blazor UI + scheduler
```

## Dependency Flow

```
DigitalButler.Web (entry point)
├── Telegram (bot service, handlers, skill executors)
├── Skills (AI routing, summarization, vault search, augmentation)
├── Context (sources: Calendar, Gmail, Personal, Obsidian)
├── Data (SQLite + Dapper repositories)
└── Common (models, enums, interfaces)
```

## Core Components

### Context Sources (`DigitalButler.Context`)
- **GoogleCalendarContextSource** - Parses iCal feeds via Ical.Net
- **GmailContextSource** - Fetches emails via MailKit
- **PersonalContextSource** - User-added notes via Telegram/UI
- **ObsidianDailyNotesContextSource** - Parses daily notes from synced Obsidian vault (YAML frontmatter + markdown)
- **ObsidianAnalysisService** - Aggregates and compares metrics from daily notes

### Skills (`DigitalButler.Skills`)
- **OpenAiSkillRouter** - Classifies user messages into skills using OpenAI Responses API; also determines vault enrichment needs
- **OpenAiSummarizationService** - Generates natural language summaries per-source
- **IAiContextAugmenter** - Optional AI-generated context enhancement
- **ICalendarEventParser** - Natural language to calendar events
- **IAudioTranscriptionService** - Whisper API for voice messages
- **IImageAnalysisService** - Vision API for image analysis
- **ISubjectTranslator** - Translates subjects to English for image search

### Vault Search (`DigitalButler.Skills/VaultSearch`)
Semantic search over the Obsidian vault using embeddings:
- **VaultSearchService** - Main search interface, deduplicates results by note
- **VaultIndexer** - Indexes vault notes into chunks with embeddings
- **EmbeddingService** - Generates embeddings via OpenAI text-embedding-3-small
- **NoteChunker** - Splits notes into semantic chunks for embedding
- **DateQueryTranslator** - Translates temporal queries ("last week", "yesterday") to date ranges
- **CitationFormatter** - Formats search results with citations

### Telegram Bot (`DigitalButler.Telegram`)
- **BotService** - IHostedService with polling (not webhooks)
- **TextMessageHandler** - Commands: `/daily`, `/weekly`, `/summary`, `/motivation`, `/activities`, `/drawref`, `/addevent`, `/add`, `/sync`, `/search`, `/vaultstats`, `/help`
- **VoiceMessageHandler** - Transcribes voice messages
- **PhotoMessageHandler** - Analyzes images
- **CallbackQueryHandler** - Handles inline keyboard button callbacks
- **ConversationStateManager** - Tracks multi-turn conversation state (pending events, drawing topics)
- **KeyboardFactory** - Builds inline keyboards for interactive UI

### Skill Executors (`DigitalButler.Telegram/Skills`)
- **SummarySkillExecutor** - Daily/weekly summaries with optional vault enrichment
- **MotivationSkillExecutor** - Personalized motivational messages
- **ActivitiesSkillExecutor** - Activity suggestions based on energy/mood
- **DrawingReferenceSkillExecutor** - Fetches drawing references from Unsplash/Pexels
- **CalendarEventSkillExecutor** - Creates Google Calendar events
- **VaultSearchSkillExecutor** - Semantic search of Obsidian vault
- **VaultEnrichmentService** - Enriches other skills with relevant vault context

### Web App (`DigitalButler.Web`)
- **SchedulerService** - Runs context updates and sends scheduled summaries
- **Blazor Admin UI** - Manage context, instructions, skills, schedules, AI settings

## Key Patterns

| Pattern | Usage |
|---------|-------|
| Repository | Data access via Dapper (not EF Core) |
| Dependency Injection | Microsoft.Extensions.DependencyInjection |
| Strategy | IContextSource, IContextUpdater, ISkillExecutor |
| Bit-mask | ContextSourceMask for per-skill context filtering |
| Hosted Service | SchedulerService, BotService for background work |
| Conversation State | ConversationStateManager for multi-turn interactions |
| Composite | CompositeDrawingReferenceService (Unsplash + Pexels) |

## Database

- **SQLite** with Dapper ORM
- **sqlite-vec** extension for vector similarity search
- Schema auto-initialized on startup (`ButlerSchemaInitializer`)

### Tables
| Table | Purpose |
|-------|---------|
| ContextItems | User context from all sources |
| Instructions | Per-source summarization instructions |
| SkillInstructions | Per-skill instructions with context source mask |
| Schedules | Context update schedules |
| SummarySchedules | Daily/weekly summary schedules |
| AiTaskSettings | Per-task AI provider configuration |
| GoogleCalendarFeeds | iCal feed URLs |
| GoogleOAuthTokens | OAuth tokens for Google Calendar API |
| AppSettings | Key-value app settings |
| ObsidianDailyNotes | Parsed daily notes with metrics |
| ObsidianWeeklySummaries | Aggregated weekly summaries |
| ContextUpdateLog | Audit log for context updates |
| VaultNotes | Indexed vault notes metadata |
| NoteChunks | Note chunks for semantic search |
| vec_note_chunks | Vector embeddings for similarity search (sqlite-vec) |

## AI Integration

- Uses **OpenAI Responses API** (not chat/completions) for structured outputs
- Configurable via env vars: `AI_BASE_URL`, `AI_MODEL`, `AI_API_KEY`
- Per-task overrides via `AiTaskSettings` table
- **Skills**: DailySummary, WeeklySummary, Motivation, Activities, DrawingReference, CalendarEvent, VaultSearch
- **Embeddings**: text-embedding-3-small (1536 dimensions) for vault search

## Configuration

### Core Environment Variables
```
AI_BASE_URL, AI_MODEL, AI_API_KEY          # AI provider
TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID       # Telegram bot
TELEGRAM_ALLOWED_USER_ID                   # Authorization
TELEGRAM_FORCE_IPV4                        # Force IPv4 for Telegram API
BUTLER_TIMEZONE                            # User timezone
BUTLER_ADMIN_USERNAME, BUTLER_ADMIN_PASSWORD  # Admin UI auth
Database__SqlitePath                       # SQLite path (default: data/butler.db)
```

### Google Calendar
```
GCAL_FEED*_NAME, GCAL_FEED*_URL           # iCal feeds (GCAL_FEED1_NAME, etc.)
GOOGLE_SERVICE_ACCOUNT_JSON                # Service account for event creation
GOOGLE_CALENDAR_ID                         # Target calendar ID
```

### Gmail
```
GMAIL_ACCOUNTS                             # Comma-separated email:password pairs
GMAIL_HOST, GMAIL_PORT, GMAIL_USE_SSL      # IMAP settings
GMAIL_UNREAD_ONLY_DEFAULT                  # Only fetch unread
GMAIL_DAYS_BACK_DEFAULT                    # Days to look back
GMAIL_MAX_MESSAGES_DEFAULT                 # Max messages per account
```

### Obsidian
```
OBSIDIAN_VAULT_PATH                        # Path to synced vault (default: /var/notes)
OBSIDIAN_DAILY_NOTES_PATTERN               # Glob pattern for daily notes
OBSIDIAN_LOOKBACK_DAYS                     # Days to scan (default: 30)
```

### Vault Search
```
OBSIDIAN_SEARCH_ENABLED                    # Enable/disable vault search
OBSIDIAN_SEARCH_MIN_SCORE                  # Minimum similarity score
OBSIDIAN_SEARCH_TOP_K                      # Number of results to return
EMBEDDING_MODEL                            # Embedding model (default: text-embedding-3-small)
OBSIDIAN_SEARCH_EXCLUDE_PATTERNS           # Comma-separated glob patterns to exclude
OBSIDIAN_SEARCH_CHUNK_SIZE                 # Target tokens per chunk
OBSIDIAN_SEARCH_BATCH_SIZE                 # Embeddings batch size
```

### Image Sources
```
UNSPLASH_ACCESS_KEY                        # Unsplash API key
PEXELS_API_KEY                             # Pexels API key
```

## Deployment

- Docker container with multi-stage build
- Persistent volume at `/data` (SQLite database, DataProtection keys)
- Obsidian vault mounted read-only at `/var/notes`
- Port 8080, restart policy: `unless-stopped`
- Entry point: `dotnet DigitalButler.Web.dll`

## Data Flow

### Scheduled Summary
1. `SchedulerService` checks `SummarySchedules` for due summaries
2. Fetches context via `ContextService.GetForWindowAsync()`
3. Applies skill-specific context filtering via `ContextSourcesMask`
4. Optionally enriches with vault search results
5. Calls `ISummarizationService.SummarizeAsync()` (per-source to prevent style leakage)
6. Sends to Telegram

### On-Demand Message
1. `TextMessageHandler` receives message
2. If plain text: `ISkillRouter.RouteWithEnrichmentAsync()` classifies intent and determines vault enrichment needs
3. Appropriate `ISkillExecutor.ExecuteAsync()` is called
4. Fetches context, applies instructions, optionally enriches with vault context
5. Summarizes and responds via Telegram

### Vault Search Flow
1. User sends `/search <query>` or natural language question
2. `DateQueryTranslator` extracts temporal references
3. `EmbeddingService` generates query embedding
4. `VaultSearchRepository` performs vector similarity search via sqlite-vec
5. Results deduplicated by note, formatted with citations
6. Response sent to Telegram

### Multi-Turn Conversations
1. `ConversationStateManager` tracks pending state (calendar events, drawing topics)
2. `CallbackQueryHandler` processes inline keyboard button presses
3. State expires after 5 minutes TTL
4. User can confirm/cancel pending actions

## Key Files

| Purpose | Path |
|---------|------|
| Entry point | `DigitalButler.Web/Program.cs` |
| Scheduler | `DigitalButler.Web/Scheduler.cs` |
| Bot lifecycle | `DigitalButler.Telegram/BotService.cs` |
| Domain models | `DigitalButler.Common/Models.cs` |
| Vault search models | `DigitalButler.Common/VaultSearchModels.cs` |
| Skill routing | `DigitalButler.Skills/SkillRouting.cs` |
| Summarization | `DigitalButler.Skills/OpenAiSummarizationService.cs` |
| Vault search | `DigitalButler.Skills/VaultSearch/VaultSearchService.cs` |
| Vault indexer | `DigitalButler.Skills/VaultSearch/VaultIndexer.cs` |
| Text commands | `DigitalButler.Telegram/Handlers/TextMessageHandler.cs` |
| Callback handler | `DigitalButler.Telegram/Handlers/CallbackQueryHandler.cs` |
| Conversation state | `DigitalButler.Telegram/State/ConversationStateManager.cs` |
| Keyboard factory | `DigitalButler.Telegram/UI/KeyboardFactory.cs` |
| Schema init | `DigitalButler.Data/ButlerSchemaInitializer.cs` |

## Coding Practices

- **Target Framework**: .NET 10.0
- **Nullable reference types**: Enabled
- **Async/await**: Used throughout
- **Logging**: Microsoft.Extensions.Logging (structured)
- **No EF Core**: Uses Dapper for explicit SQL control
- **No migrations**: Schema initialized on startup with idempotent ALTER TABLE
- **Per-source summarization**: Prevents instruction/style leakage between sources
- **Telegram polling**: No webhook, simpler deployment
- **Exponential backoff**: Bot reconnects gracefully on connectivity issues
- **TTL-based state**: Conversation state expires after 5 minutes

## Testing

No test project currently. Key areas to test:
- Skill routing (text classification and vault enrichment detection)
- Summarization (prompt generation + response parsing)
- Context source updates (iCal parsing, email fetching)
- Command parsing in message handlers
- Scheduler logic (time windows)
- Vault indexing and search (chunking, embeddings, similarity)
- Date query translation

## Dependencies

- **Telegram.Bot** - Telegram API client
- **Dapper** - Lightweight ORM
- **Microsoft.Data.Sqlite** - SQLite provider
- **sqlite-vec** - Vector similarity extension for SQLite
- **Ical.Net** - iCal parsing
- **MailKit** - Email protocol
- **Google.Apis.Calendar.v3** - Google Calendar API
