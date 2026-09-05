using System.Text.Json;
using Lumio.Client.Replica.Tests.Support;

namespace Lumio.Client.Replica.Tests.Contract;

public sealed class GameplayEnvelopeContractTests
{
    [Fact]
    public void FrozenC1ContractIsGameplayEnvelopeNotHelloWire()
    {
        using JsonDocument document = LoadGameplay();
        JsonElement root = document.RootElement;
        Assert.Equal("lumio.gameplay-envelope.v1", root.GetProperty("contractId").GetString());
        Assert.NotEqual("lumio.hello-wire.v1", root.GetProperty("contractId").GetString());
        Assert.Equal("utf8-json-text-frame", root.GetProperty("transport").GetProperty("encoding").GetString());
        Assert.True(root.GetProperty("mappings").TryGetProperty("chat.input", out _));
        Assert.True(root.GetProperty("mappings").TryGetProperty("field.write", out _));
        Assert.False(root.GetProperty("mappings").TryGetProperty("chat.event", out _));
        Assert.False(root.GetProperty("mappings").TryGetProperty("chat.component", out _));
        Assert.Equal(
            new[] { "Welcome", "WorldChange", "InputCommand", "ConnectionSuperseded", "Error" },
            root.GetProperty("messages").EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Contains("chat_text_too_long", root.GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(512, root.GetProperty("boundedInput").GetProperty("rules").GetProperty("chatTextMaxUtf8Bytes").GetInt32());
    }

    [Fact]
    public void FrozenC1HashExampleMatchesLocalLumioBinV1Encoder()
    {
        using JsonDocument document = LoadGameplay();
        string payload = GameplayWireFixtures.ChatInputPayload;
        string sha = GameplayWireFixtures.ChatInputSha256;

        JsonElement example = document.RootElement.GetProperty("hash").GetProperty("examples")
            .EnumerateArray()
            .First(e => e.GetProperty("mappingId").GetString() == "chat.input");

        Assert.Equal(payload, example.GetProperty("payload").GetString());
        Assert.Equal(sha, example.GetProperty("payloadSha256").GetString());
    }

    [Fact]
    public void FrozenC1ClientRpcRecordCarriesOrderedRuntimeIdentity()
    {
        using JsonDocument document = LoadGameplay();
        string[] required = document.RootElement
            .GetProperty("sharedTypes")
            .GetProperty("ClientRpcRecord")
            .GetProperty("required")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            new[] { "target", "componentId", "method", "args", "messageId", "roomSequence", "sender", "appliedTick" },
            required);
    }

    private static JsonDocument LoadGameplay()
    {
        string? path = WireContractLocator.LocateGameplayEnvelope();
        if (path is null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "gameplay-command-envelope-v1.json not found; need architecture origin/main 2b7e321 or LUMIO_GAMEPLAY_ENVELOPE_CONTRACT.");
        }

        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
