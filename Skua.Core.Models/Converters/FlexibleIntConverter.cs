using System.Globalization;
using Newtonsoft.Json;

namespace Skua.Core.Models.Converters;

/// <summary>
/// Converte números JSON inteiros mesmo quando
/// serializados com ponto decimal, por exemplo:
/// 1369973542.0.
/// </summary>
public sealed class FlexibleIntConverter
    : JsonConverter<int>
{
    public override int ReadJson(
        JsonReader reader,
        Type objectType,
        int existingValue,
        bool hasExistingValue,
        JsonSerializer serializer
    )
    {
        switch (reader.TokenType)
        {
            case JsonToken.Integer:
                return Convert.ToInt32(
                    reader.Value,
                    CultureInfo.InvariantCulture
                );

            case JsonToken.Float:
            case JsonToken.String:
            {
                string? text =
                    Convert.ToString(
                        reader.Value,
                        CultureInfo.InvariantCulture
                    );

                bool valid =
                    decimal.TryParse(
                        text,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out decimal number
                    )
                    && number ==
                        decimal.Truncate(number)
                    && number >= int.MinValue
                    && number <= int.MaxValue;

                if (valid)
                    return decimal.ToInt32(number);

                throw new JsonSerializationException(
                    $"O valor '{text}' não pode " +
                    "ser convertido para Int32."
                );
            }

            case JsonToken.Null:
                return 0;

            default:
                throw new JsonSerializationException(
                    $"Token JSON não suportado para " +
                    $"Int32: {reader.TokenType}."
                );
        }
    }

    public override void WriteJson(
        JsonWriter writer,
        int value,
        JsonSerializer serializer
    )
    {
        writer.WriteValue(value);
    }
}
