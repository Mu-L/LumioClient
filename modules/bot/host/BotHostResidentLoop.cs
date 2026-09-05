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
        _ = encodedBytes;
        if (!string.Equals(message.MappingId, WireCodec.ChatInput, StringComparison.Ordinal))
        {
            return;
        }

        ulong tick = _pendingTicks.Count == 0 ? 0UL : _pendingTicks.Dequeue();
        BotHostResidentLoop.AppendChatInputLog(_logPath, tick, _accountId, message, encodedBytes);
    }
}

internal static class BotHostResidentLoop
{
    private const int CadenceBatchCount = 3;

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

            if (cadenceTick >= ClientTimerManager.BotChatCadenceTicks * 3UL)
            {
                await delay(cancellationToken);
                continue;
            }

            cadenceTick++;
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
                }
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
        _ = encodedBytes;
        string payloadSha256 = Convert.ToHexString(SHA256.HashData(message.Payload.Span)).ToLowerInvariant();
        string line = "{\"ts\":\"" + DateTime.UtcNow.ToString("o") +
                      "\",\"kind\":\"chat.input\",\"tick\":" + tick.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      ",\"tickSource\":\"native-kernel/tickFrame\",\"pid\":" +
                      Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      ",\"accountId\":" + JsonSerializer.Serialize(accountId) +
                      ",\"messageType\":\"InputCommand\",\"mappingId\":" + JsonSerializer.Serialize(message.MappingId) +
                      ",\"payloadSha256\":\"" + payloadSha256 + "\"}\n";
        File.AppendAllText(path, line);
    }
}
