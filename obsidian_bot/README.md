# Obsidian Bot

Standalone Telegram bot that only saves incoming content to your Obsidian vault.

## Features
- Handles plain text, photo, and voice messages
- `/add <text>` shortcut
- Destination picker:
  - today's daily note
  - custom date daily note (`YYYY-MM-DD`)
  - inbox note
- Stores photo/voice files in configured media folder and embeds them into notes

## Environment variables
- `TELEGRAM_BOT_TOKEN` (required)
- `TELEGRAM_ALLOWED_USER_ID` (required)
- `BUTLER_TIMEZONE` (optional, default: `UTC`)
- `OBSIDIAN_VAULT_PATH` (optional, default: `/var/notes`)
- `OBSIDIAN_DAILY_NOTES_PATTERN` (optional, default: `04 archive/journal/daily journal/*.md`)
- `OBSIDIAN_INBOX_NOTE_PATH` (optional, default: `_inbox/_inbox.md`)
- `OBSIDIAN_MEDIA_FOLDER_PATH` (optional, default: `_inbox`)

## Run locally
```bash
dotnet run --project obsidian_bot/ObsidianBot.csproj
```

## Run with Docker Compose
From repo root:
```bash
docker compose -f obsidian_bot/docker-compose.yml up --build -d
```
