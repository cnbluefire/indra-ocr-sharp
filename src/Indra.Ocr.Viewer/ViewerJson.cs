using System.Text.Json.Serialization;

namespace Indra.Ocr.Viewer;

/// <summary>
/// The viewer's JSON, generated at compile time — the plan's AOT-friendly choice: no reflection,
/// no runtime serializer construction. Names come from the camelCase policy plus the explicit
/// <c>[JsonPropertyName]</c>s that keep the reference's snake_case keys.
/// </summary>
/// <remarks>
/// The reference spells its answers with <c>json.dumps</c>, which puts a space after every comma
/// and colon and escapes non-ASCII; this writes the same names and the same values without those
/// spaces. That is why the HTTP harness (step 240) compares the answers as parsed JSON rather
/// than as bytes — the one place in the port where the output is not byte-comparable on purpose.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(PageResponse))]
[JsonSerializable(typeof(PageRequest))]
internal sealed partial class ViewerJson : JsonSerializerContext;
