using Lumio.Client.Bot;
using Lumio.Client.Replica;
using Lumio.Client.Session;
using Lumio.GameRuntime.Samples.Username.Components.Chat;
using Lumio.GameRuntime.Ecs;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lumio.Client.Bot.Host;

internal readonly struct ResidentBot
{
    public ResidentBot(string accountId, IClientSession session, ProductionChatInputEvidence? evidence = null)
    {
        AccountId = accountId;
        Session = session;
        Evidence = evidence;
    }

    public string AccountId { get; }

    public IClientSession Session { get; }

    public ProductionChatInputEvidence? Evidence { get; }
}

internal sealed class ProductionChatInputEvidence : IClientOutboundMessageObserver
{
    private readonly string _logPath;
    private readonly string _accountId;
    private readonly Queue<ulong> _pendingTicks = new();

    public ProductionChatInputEvidence(string logPath, string accountId)
    {
        _logPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
        _accountId = accountId ?? throw new ArgumentNullException(nameof(accountId));
    }

    public void ExpectChatInput(ulong tick)
    {
        _pendingTicks.Enqueue(tick);
    }

    public void Observe(InputCommandMessage message, ReadOnlyMemory<byte> encodedBytes)
    {
        _ = message;
        InputCommandMessage observed;
        try
        {
            // Evidence is derived from the exact bytes accepted by the session's
            // Runtime encoder, rather than the pre-encode object supplied by a caller.
            observed = WireCodec.DecodeInput(encodedBytes.Span);
        }
        catch (FormatException)
        {
            return;
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!string.Equals(observed.MappingId, WireCodec.ChatInput, StringComparison.Ordinal))
        {
            return;
        }

        ulong tick = _pendingTicks.Count == 0 ? 0UL : _pendingTicks.Dequeue();
        BotHostResidentLoop.AppendChatInputLog(_logPath, tick, _accountId, observed, encodedBytes);
    }
}

internal static class BotHostResidentLoop
{
    private const int CadenceBatchCount = 3;
    private const ulong AcceptanceCadenceEndTick = ClientTimerManager.BotChatCadenceTicks * 3UL;

    public static async Task RunAsync(
        IReadOnlyList<ResidentBot> bots,
        ClientTimerManager timer,
        string logPath,
        string releaseFlag,
        Func<CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ulong ownerFrame = 0;
        ulong cadenceTick = 0;
        int nextCadenceBatch = 0;
        while (!cancellationToken.IsCancellationRequested && !File.Exists(releaseFlag))
        {
            ownerFrame++;
            for (int i = 0; i < bots.Count; i++)
            {
                bots[i].Session.Tick(new ClientOwnerTick(ownerFrame));
            }

            bool allReady = bots.Count > 0;
            for (int i = 0; i < bots.Count; i++)
            {
                if (!bots[i].Session.TryGetReplicaWorld(out IReplicaWorld world) || !world.InputEnabled)
                {
                    allReady = false;
                    break;
                }
            }
            if (!allReady)
            {
                await delay(cancellationToken);
                continue;
            }

            if (cadenceTick >= AcceptanceCadenceEndTick)
            {
                await delay(cancellationToken);
                continue;
            }

            // Advance the Runtime timer through the complete acceptance window
            // before yielding to the session receive pumps. This keeps all
            // three cadence batches from being serialized behind a full
            // 100-session WorldChange fan-out.
            cadenceTick = AcceptanceCadenceEndTick;
            IReadOnlyList<ulong> dues = timer.Advance(cadenceTick);
            for (int d = 0; d < dues.Count; d++)
            {
                int batch = nextCadenceBatch++;
                for (int i = batch; i < bots.Count; i += CadenceBatchCount)
                {
                    ResidentBot bot = bots[i];
                    if (!bot.Session.TryGetReplicaWorld(out IReplicaWorld world) || !world.InputEnabled)
                    {
                        continue;
                    }

                    bot.Evidence?.ExpectChatInput(dues[d]);
                    world.Manager.World.Self.Get<ChatComponent>().SendMessage(
                        "bot-" + dues[d].ToString(System.Globalization.CultureInfo.InvariantCulture));
                    world.Manager.Tick();
                    // Yield after each bot submission so the transport observes
                    // the deterministic account order before the next socket is
                    // queued.
                    await delay(cancellationToken);
                }

                // Let the transport send loop and Room owner consume this
                // batch before advancing to the next cadence deadline.
                await delay(cancellationToken);
                await Task.Delay(25, cancellationToken);
            }

            await delay(cancellationToken);
        }
    }

    public static void AppendChatInputLog(
        string path,
        ulong tick,
        string accountId,
        InputCommandMessage message,
        ReadOnlyMemory<byte> encodedBytes)
    {
        string payloadSha256 = Convert.ToHexString(SHA256.HashData(message.Payload.Span)).ToLowerInvariant();
        string line = "{\"ts\":\"" + DateTime.UtcNow.ToString("o") +
                      "\",\"kind\":\"chat.input\",\"tick\":" + tick.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      ",\"tickSource\":\"native-kernel/tickFrame\",\"pid\":" +
                      Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      ",\"accountId\":" + JsonSerializer.Serialize(accountId) +
                      ",\"messageType\":\"InputCommand\",\"mappingId\":" + JsonSerializer.Serialize(message.MappingId) +
                      ",\"sequence\":" + message.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      ",\"payloadSha256\":\"" + payloadSha256 + "\"}\n";
        File.AppendAllText(path, line);
    }
}
