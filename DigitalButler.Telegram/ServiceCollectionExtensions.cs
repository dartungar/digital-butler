using DigitalButler.Common;
using DigitalButler.Telegram.Handlers;
using DigitalButler.Telegram.Skills;
using DigitalButler.Telegram.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalButler.Telegram;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramSkillExecutors(this IServiceCollection services)
    {
        services.TryAddScoped<ISummarySkillExecutor, SummarySkillExecutor>();
        services.TryAddScoped<IMotivationSkillExecutor, MotivationSkillExecutor>();
        services.TryAddScoped<IActivitiesSkillExecutor, ActivitiesSkillExecutor>();
        services.TryAddScoped<ICalendarEventSkillExecutor, CalendarEventSkillExecutor>();
        services.TryAddScoped<IVaultSearchSkillExecutor, VaultSearchSkillExecutor>();

        return services;
    }

    public static IServiceCollection AddTelegramBot(this IServiceCollection services)
    {
        // Error notifier (singleton - shared across scheduler and handlers)
        services.TryAddSingleton<ITelegramErrorNotifier, TelegramErrorNotifier>();

        // State management (singleton - shared across all handlers)
        services.TryAddSingleton<ConversationStateManager>();

        services.AddTelegramSkillExecutors();

        // Message handlers (scoped - one per request)
        services.AddScoped<ITextMessageHandler, TextMessageHandler>();
        services.AddScoped<IVoiceMessageHandler, VoiceMessageHandler>();
        services.AddScoped<IPhotoMessageHandler, PhotoMessageHandler>();
        services.AddScoped<ICallbackQueryHandler, CallbackQueryHandler>();

        // Bot service (hosted service)
        services.AddHostedService<BotService>();

        return services;
    }
}
