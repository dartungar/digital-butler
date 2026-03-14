# Drawing Bot

Standalone Telegram bot for drawing references.

## Features
- `/drawref <subject>` command
- Subject translation to English (optional, via OpenAI Responses API)
- Random topic suggestion when no subject is provided
- Inline buttons:
  - Confirm random topic
  - Suggest another topic
  - Different image
  - Try other source (Unsplash/Pexels)
  - Different subject

## Environment Variables
- `TELEGRAM_BOT_TOKEN` (required)
- `TELEGRAM_ALLOWED_USER_ID` (required)
- `AI_BASE_URL` (optional, default `https://api.openai.com/v1`)
- `AI_MODEL` (optional)
- `AI_API_KEY` (optional)
- `UNSPLASH_ACCESS_KEY` (optional but recommended)
- `PEXELS_API_KEY` (optional but recommended)

At least one of `UNSPLASH_ACCESS_KEY` or `PEXELS_API_KEY` must be configured.

## Run
```bash
dotnet run --project drawing_bot/DrawingBot.csproj
```
