# DigitalButler Context

Shared domain, EF Core context, and AI helpers for the digital butler.

## Models
- `ContextItem`: stored context from sources (Google Calendar, Gmail, Personal, etc.).
- `Instruction`: per-source instructions.
- `ScheduleConfig`: per-source fetch schedule placeholder.
- `SummarySchedule`: daily/weekly summary times.
- `AiTaskSetting`: per-task AI provider/model/key overrides.

## Services
- `ButlerDbContext`: EF Core SQLite context.
- `IContextSource`/`IContextUpdater`: abstractions for context sources; `ContextUpdater` provides upsert logic.
- `ISummarizationService`: summarization abstraction implemented by `OpenAiSummarizationService`.
- `AiSettingsResolver`: merges DB overrides with env defaults.

## Configuration
Env defaults for AI are read in web app:
- `AI_BASE_URL`
- `AI_MODEL`
- `AI_API_KEY`

