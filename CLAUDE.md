# AGENTS.md - Digital Butler

## Project Overview

Digital Butler is a personal AI assistant Telegram bot built with .NET 10.0. It aggregates information from multiple context sources (Google Calendar, Gmail, personal notes) and uses AI to provide daily summaries, motivational messages, and personalized recommendations.

## Solution Structure

```
DigitalButler.sln
├── DigitalButler.Common          # Shared domain models & interfaces
├── DigitalButler.Data            # SQLite database & repositories (Dapper)
├── DigitalButler.Context         # Context sources & updaters
├── DigitalButler.Skills          # AI services & skill routing
├── DigitalButler.Telegram        # Telegram bot implementation
└── DigitalButler.Web             # ASP.NET Core Blazor UI + scheduler
```

## Dependency Flow

```
DigitalButler.Web (entry point)
├── Telegram (bot service, handlers, skill executors)
├── Skills (AI routing, summarization, augmentation)
├── Context (sources: Calendar, Gmail, Personal)
├── Data (SQLite + Dapper repositories)
└── Common (models, enums, interfaces)
```

## Core Components

### Context Sources (`DigitalButler.Context`)
- **GoogleCalendarContextSource** - Parses iCal feeds via Ical.Net
- **GmailContextSource** - Fetches emails via MailKit
- **PersonalContextSource** - User-added notes via Telegram/UI
- **ObsidianDailyNotesContextSource** - Parses daily notes from synced Obsidian vault (YAML frontmatter + markdown)

### Skills (`DigitalButler.Skills`)
- **OpenAiSkillRouter** - Classifies user messages into skills using OpenAI Responses API
- **OpenAiSummarizationService** - Generates natural language summaries per-source
- **IAiContextAugmenter** - Optional AI-generated context enhancement
- **ICalendarEventParser** - Natural language to calendar events
- **IAudioTranscriptionService** - Whisper API for voice
- **IImageAnalysisService** - Vision API for images

### Telegram Bot (`DigitalButler.Telegram`)
- **BotService** - IHostedService with polling (not webhooks)
- **TextMessageHandler** - Commands: `/daily`, `/weekly`, `/motivation`, `/activities`, `/drawref`, `/addevent`, `/add`, `/sync`, `/help`
- **VoiceMessageHandler** - Transcribes voice messages
- **PhotoMessageHandler** - Analyzes images
- **Skill Executors** - Telegram-specific implementations of each skill

### Web App (`DigitalButler.Web`)
- **SchedulerService** - Runs context updates and sends scheduled summaries
- **Blazor Admin UI** - Manage context, instructions, skills, schedules

## Key Patterns

| Pattern | Usage |
|---------|-------|
| Repository | Data access via Dapper (not EF Core) |
| Dependency Injection | Microsoft.Extensions.DependencyInjection |
| Strategy | IContextSource, IContextUpdater, ISkillExecutor |
| Bit-mask | ContextSourceMask for per-skill context filtering |
| Hosted Service | SchedulerService, BotService for background work |

## Database

- **SQLite** with Dapper ORM
- Schema auto-initialized on startup (`ButlerSchemaInitializer`)
- Tables: `ContextItems`, `Instructions`, `SkillInstructions`, `Schedules`, `SummarySchedules`, `AiTaskSettings`, `GoogleCalendarFeeds`, `GoogleOAuthTokens`, `AppSettings`, `ObsidianDailyNotes`, `ObsidianWeeklySummaries`, `ContextUpdateLog`

## AI Integration

- Uses **OpenAI Responses API** (not chat/completions) for structured outputs
- Configurable via env vars: `AI_BASE_URL`, `AI_MODEL`, `AI_API_KEY`
- Per-task overrides via `AiTaskSettings` table
- Skills: Summary, Motivation, Activities, DrawingReference, CalendarEvent

## Configuration

Key environment variables:
```
AI_BASE_URL, AI_MODEL, AI_API_KEY          # AI provider
TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID       # Telegram bot
TELEGRAM_ALLOWED_USER_ID                   # Authorization
BUTLER_TIMEZONE                            # User timezone
GCAL_FEED*_NAME, GCAL_FEED*_URL           # iCal feeds
GMAIL_ACCOUNTS                             # Email accounts
GOOGLE_SERVICE_ACCOUNT_JSON                # Calendar event creation
UNSPLASH_ACCESS_KEY                        # Drawing references
OBSIDIAN_VAULT_PATH                        # Path to synced vault (default: /var/notes)
OBSIDIAN_DAILY_NOTES_PATTERN               # Glob pattern (default: 04 archive/journal/daily notes/*.md)
OBSIDIAN_LOOKBACK_DAYS                     # Days to scan (default: 30)
Database__SqlitePath                       # SQLite path (default: data/butler.db)
```

## Deployment

- Docker container with multi-stage build
- Persistent volume at `/data/butler.db`
- Port 8080, restart policy: `unless-stopped`
- Entry point: `dotnet DigitalButler.Web.dll`

## Data Flow

### Scheduled Summary
1. `SchedulerService` checks `SummarySchedules` for due summaries
2. Fetches context via `ContextService.GetForWindowAsync()`
3. Applies skill-specific context filtering via `ContextSourcesMask`
4. Calls `ISummarizationService.SummarizeAsync()` (per-source to prevent style leakage)
5. Sends to Telegram

### On-Demand Message
1. `TextMessageHandler` receives message
2. If plain text: `ISkillRouter.RouteAsync()` classifies intent
3. Appropriate `ISkillExecutor.ExecuteAsync()` is called
4. Fetches context, applies instructions, summarizes
5. Responds via Telegram

## Key Files

| Purpose | Path |
|---------|------|
| Entry point | `DigitalButler.Web/Program.cs` |
| Scheduler | `DigitalButler.Web/Scheduler.cs` |
| Bot lifecycle | `DigitalButler.Telegram/BotService.cs` |
| Domain models | `DigitalButler.Common/Models.cs` |
| Skill routing | `DigitalButler.Skills/SkillRouting.cs` |
| Summarization | `DigitalButler.Skills/OpenAiSummarizationService.cs` |
| Text commands | `DigitalButler.Telegram/Handlers/TextMessageHandler.cs` |
| Schema init | `DigitalButler.Data/ButlerSchemaInitializer.cs` |

## Coding Practices

- **Target Framework**: .NET 10.0
- **Nullable reference types**: Enabled
- **Async/await**: Used throughout
- **Logging**: Microsoft.Extensions.Logging (structured)
- **No EF Core**: Uses Dapper for explicit SQL control
- **No migrations**: Schema initialized on startup
- **Per-source summarization**: Prevents instruction/style leakage between sources
- **Telegram polling**: No webhook, simpler deployment
- **Exponential backoff**: Bot reconnects gracefully on connectivity issues

## Testing

No test project currently. Key areas to test:
- Skill routing (text classification)
- Summarization (prompt generation + response parsing)
- Context source updates (iCal parsing, email fetching)
- Command parsing in message handlers
- Scheduler logic (time windows)

## Dependencies

- **Telegram.Bot** - Telegram API client
- **Dapper** - Lightweight ORM
- **Microsoft.Data.Sqlite** - SQLite provider
- **Ical.Net** - iCal parsing
- **MailKit** - Email protocol
- **Google.Apis.Calendar.v3** - Google Calendar API
