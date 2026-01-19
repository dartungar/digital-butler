using DigitalButler.Common;
using DigitalButler.Telegram.Handlers;
using DigitalButler.Telegram.Skills;
using DigitalButler.Telegram.State;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalButler.Telegram;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramBot(this IServiceCollection services)
    {
        // Error notifier (singleton - shared across scheduler and handlers)
        services.AddSingleton<ITelegramErrorNotifier, TelegramErrorNotifier>();

        // State management (singleton - shared across all handlers)
        services.AddSingleton<ConversationStateManager>();

        // Skill executors (scoped - one per request)
        services.AddScoped<ISummarySkillExecutor, SummarySkillExecutor>();
        services.AddScoped<IMotivationSkillExecutor, MotivationSkillExecutor>();
        services.AddScoped<IActivitiesSkillExecutor, ActivitiesSkillExecutor>();
        services.AddScoped<IDrawingReferenceSkillExecutor, DrawingReferenceSkillExecutor>();
        services.AddScoped<ICalendarEventSkillExecutor, CalendarEventSkillExecutor>();

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
