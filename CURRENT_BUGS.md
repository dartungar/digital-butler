- when asking for motivation, bot says "no motivation available"
- same with activities - "No activities available."
- when using "another one" in bot, I get "Request failed: Bad Request: message text is empty"
- when using "another image" button in drawing reference conversation, it gets the same image from Unsplash
- error when sending weekly summaries and errors to group where I added the bot (and made him admin!)- "Received HTTP response headers after 4628.3854ms - 200
info: System.Net.Http.HttpClient.ISummarizationService.LogicalHandler[101]
End processing HTTP request after 4628.465ms - 200
info: System.Net.Http.HttpClient.ISummarizationService.ClientHandler[101]
Received HTTP response headers after 8680.8946ms - 200
info: System.Net.Http.HttpClient.ISummarizationService.LogicalHandler[101]
End processing HTTP request after 8681.0866ms - 200
fail: DigitalButler.Web.SchedulerService[0]
Failed to send weekly summary
Telegram.Bot.Exceptions.ApiRequestException: Bad Request: chat not found
at Telegram.Bot.TelegramBotClient.MakeRequestAsync[TResponse](IRequest`1 request, CancellationToken cancellationToken)
at Telegram.Bot.TelegramBotClientExtensions.SendTextMessageAsync(ITelegramBotClient botClient, ChatId chatId, String text, Nullable`1 messageThreadId, Nullable`1 parseMode, IEnumerable`1 entities, Nullable`1 disableWebPagePreview, Nullable`1 disableNotification, Nullable`1 protectContent, Nullable`1 replyToMessageId, Nullable`1 allowSendingWithoutReply, IReplyMarkup replyMarkup, CancellationToken cancellationToken)
at DigitalButler.Web.SchedulerService.SendSummaryAsync(IServiceProvider serviceProvider, ContextService contextService, InstructionService instructionService, SkillInstructionService skillInstructionService, ISummarizationService summarizer, IAiContextAugmenter aiContext, TimeZoneInfo tz, String taskName, String chatId, CancellationToken ct) in /src/DigitalButler.Web/Scheduler.cs:line 274
at DigitalButler.Web.SchedulerService.TickAsync(CancellationToken ct) in /src/DigitalButler.Web/Scheduler.cs:line 193
warn: DigitalButler.Telegram.TelegramErrorNotifier[0]
Failed to send error notification to Telegram
Telegram.Bot.Exceptions.ApiRequestException: Bad Request: chat not found
at Telegram.Bot.TelegramBotClient.MakeRequestAsync[TResponse](IRequest`1 request, CancellationToken cancellationToken)
at Telegram.Bot.TelegramBotClientExtensions.SendTextMessageAsync(ITelegramBotClient botClient, ChatId chatId, String text, Nullable`1 messageThreadId, Nullable`1 parseMode, IEnumerable`1 entities, Nullable`1 disableWebPagePreview, Nullable`1 disableNotification, Nullable`1 protectContent, Nullable`1 replyToMessageId, Nullable`1 allowSendingWithoutReply, IReplyMarkup replyMarkup, CancellationToken cancellationToken)
at DigitalButler.Telegram.TelegramErrorNotifier.SendAsync(String message, CancellationToken ct) in /src/DigitalButler.Telegram/TelegramErrorNotifier.cs:line 65"
- raw markdown in Telegram messages looks ugly, is there a way to apply bold, italics etc