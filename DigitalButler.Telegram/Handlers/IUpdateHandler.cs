using Telegram.Bot;
using Telegram.Bot.Types;

namespace DigitalButler.Telegram.Handlers;

public interface ITextMessageHandler
{
    Task HandleAsync(ITelegramBotClient bot, Message message, CancellationToken ct);
}

public interface IVoiceMessageHandler
{
    Task HandleAsync(ITelegramBotClient bot, Message message, CancellationToken ct);
}

public interface IPhotoMessageHandler
{
    Task HandleAsync(ITelegramBotClient bot, Message message, CancellationToken ct);
}

public interface ICallbackQueryHandler
{
    Task HandleAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, CancellationToken ct);
}
