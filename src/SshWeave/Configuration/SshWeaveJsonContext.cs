using System.Text.Json.Serialization;

namespace SshWeave.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SshWeaveConfiguration))]
[JsonSerializable(typeof(EncryptedConnectionPayload))]
internal sealed partial class SshWeaveJsonContext : JsonSerializerContext;
