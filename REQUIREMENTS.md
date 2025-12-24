This is a digital butler that uses various contexts 
(calendar events, e-mails, and personal context, all stored in SQLite Database) 
to provide information and advice:
- as daily/weekly agenda
- on-demand
- 
For daily summary, app sends a summary of relevant context for the upcoming day to the user via Telegram at a scheduled time.
For on-demand, user can just message the bot via Telegram to get a summary of relevant context at any time.

AI is used to process and summarize the context before sending it to the user.
AI model and API keys are set via environment variables.

##### Context
App has access to several sources of context:
- Google Calendar events
- E-mails from Gmail
- Personal context added by user
- potentially more sources, like weather, air pollution, news, etc.

Each external context source (called "Context", see DigitalButler.Context project) has its own updater that runs on a schedule 
(configurable via Admin UI) to pull new data and update the database.

Information from sources is pulled via APIs, processed (categorized/summarized) via AI, and stored in the database.
Personal context can be added by user via Telegram or Admin UI and stored in the database.
All context can be:
- date-relevant
- "timeless" (not date-relevant)

##### Admin UI
App has an admin UI where I can:
- view and edit stored context
- set custom instructions for the butler (e.g how each context source should be processed)
- configure schedule of context updating and daily/weekly summaries

Admin UI should be implemented as a web app using Blazor or Razor Pages.

##### Telegram Bot
App has a Telegram bot that:
- sends daily summary at scheduled time
- responds to user messages with on-demand summary
- allows adding personal context via messages

Telegram Bot should be run as a background service.

##### Deployment
App is deployed as Docker container
