using Telegram.Bot.Types.ReplyMarkups;

namespace DigitalButler.Telegram.UI;

public static class KeyboardFactory
{
    public static ReplyKeyboardMarkup BuildMainReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/daily"), new KeyboardButton("/weekly") },
            new[] { new KeyboardButton("/motivation"), new KeyboardButton("/activities") },
            new[] { new KeyboardButton("/add ") }
        })
        {
            ResizeKeyboard = true
        };
    }

    public static InlineKeyboardMarkup BuildHelpInlineKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Daily Summary", "help:daily"),
                InlineKeyboardButton.WithCallbackData("Weekly Summary", "help:weekly")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Motivation", "help:motivation"),
                InlineKeyboardButton.WithCallbackData("Activities", "help:activities")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Drawing Ref", "help:drawref"),
                InlineKeyboardButton.WithCallbackData("Add Event", "help:addevent")
            }
        });
    }

    public static InlineKeyboardMarkup BuildSummaryChoiceKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Daily", "summary:daily"),
                InlineKeyboardButton.WithCallbackData("Weekly", "summary:weekly")
            }
        });
    }

    public static InlineKeyboardMarkup BuildSummaryRefreshKeyboard(bool isWeekly)
    {
        var refreshData = isWeekly ? "summary:refresh_weekly" : "summary:refresh_daily";
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Refresh", refreshData)
            }
        });
    }

    public static InlineKeyboardMarkup BuildMotivationRefreshKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Another One", "motivation:refresh")
            }
        });
    }

    public static InlineKeyboardMarkup BuildActivitiesRefreshKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Refresh", "activities:refresh")
            }
        });
    }

    public static InlineKeyboardMarkup BuildDrawingTopicConfirmationKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Yes, let's go!", "drawref:confirm"),
                InlineKeyboardButton.WithCallbackData("Suggest another", "drawref:another")
            }
        });
    }

    public static InlineKeyboardMarkup BuildDrawingResultKeyboard(string? currentSource = null)
    {
        var tryOtherLabel = currentSource switch
        {
            "unsplash" => "Try Pexels",
            "pexels" => "Try Unsplash",
            _ => "Try Other Site"
        };

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Different Image", "drawref:different_image"),
                InlineKeyboardButton.WithCallbackData(tryOtherLabel, "drawref:try_other_source")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Different Subject", "drawref:different_subject")
            }
        });
    }

    public static InlineKeyboardMarkup BuildEventConfirmationKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Create Event", "calevent:confirm"),
                InlineKeyboardButton.WithCallbackData("Cancel", "calevent:reject")
            }
        });
    }
}
