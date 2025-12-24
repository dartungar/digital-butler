using Dapper;
using System.Data;
using System.Globalization;

namespace DigitalButler.Context.Data;

public static class DapperTypeHandlers
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new GuidHandler());
        SqlMapper.AddTypeHandler(new NullableGuidHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
    }

    private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            parameter.Value = value.ToString("D", CultureInfo.InvariantCulture);
            parameter.DbType = DbType.String;
        }

        public override Guid Parse(object value)
        {
            return value switch
            {
                Guid g => g,
                string s => Guid.Parse(s),
                byte[] bytes when bytes.Length == 16 => new Guid(bytes),
                ReadOnlyMemory<byte> rom when rom.Length == 16 => new Guid(rom.ToArray()),
                _ => throw new DataException($"Cannot convert {value.GetType()} to Guid")
            };
        }
    }

    private sealed class NullableGuidHandler : SqlMapper.TypeHandler<Guid?>
    {
        public override void SetValue(IDbDataParameter parameter, Guid? value)
        {
            if (value is null)
            {
                parameter.Value = DBNull.Value;
                return;
            }

            parameter.Value = value.Value.ToString("D", CultureInfo.InvariantCulture);
            parameter.DbType = DbType.String;
        }

        public override Guid? Parse(object value)
        {
            if (value is null || value is DBNull)
            {
                return null;
            }

            return value switch
            {
                Guid g => g,
                string s => Guid.Parse(s),
                byte[] bytes when bytes.Length == 16 => new Guid(bytes),
                ReadOnlyMemory<byte> rom when rom.Length == 16 => new Guid(rom.ToArray()),
                _ => throw new DataException($"Cannot convert {value.GetType()} to Guid?")
            };
        }
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.Value = value.ToString("O", CultureInfo.InvariantCulture);
            parameter.DbType = DbType.String;
        }

        public override DateTimeOffset Parse(object value)
        {
            return value switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset")
            };
        }
    }

    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.Value = value.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture);
            parameter.DbType = DbType.String;
        }

        public override TimeOnly Parse(object value)
        {
            return value switch
            {
                TimeOnly t => t,
                string s => TimeOnly.Parse(s, CultureInfo.InvariantCulture),
                _ => throw new DataException($"Cannot convert {value.GetType()} to TimeOnly")
            };
        }
    }
}
