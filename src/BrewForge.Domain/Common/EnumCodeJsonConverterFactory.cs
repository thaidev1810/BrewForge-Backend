using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrewForge.Domain.Common;

/// <summary>
/// Reads and writes every enum as the code the data dictionary lists for it
/// (<c>RD_MANAGER</c>, <c>FINISH_TO_START</c>, <c>ml</c>), so the API and the JSON
/// documents stored in the database speak the same vocabulary as its columns.
/// </summary>
public sealed class EnumCodeJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && EnumCode<T>.TryParse(reader.GetString(), out var value))
            {
                return value;
            }
            throw new JsonException($"must be one of: {string.Join(", ", EnumCode<T>.AllCodes)}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(EnumCode<T>.ToCode(value));
    }
}
