using Lumio.Client.Bot;
using Lumio.Client.Replica;
using Lumio.Client.Session;
using Lumio.GameRuntime.Samples.Username.Components.Chat;

namespace Lumio.Client.Bot.Host;

internal readonly struct ResidentBot
{
    public ResidentBot(string accountId, IClientSession session)
    {
        AccountId = accountId;
        Session = session;
    }

    public string AccountId { get; }

    public IClientSession Session { get; }
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

                    world.Manager.World.Self.Get<ChatComponent>().SendMessage(
                        "bot-" + dues[d].ToString(System.Globalization.CultureInfo.InvariantCulture));
                    world.Manager.Tick();
                    AppendChatInputLog(logPath, dues[d], bot.AccountId);
                }
            }

            await delay(cancellationToken);
        }
    }

    public static void AppendChatInputLog(string path, ulong tick, string accountId)
    {
        string line = "{\"ts\":\"" + DateTime.UtcNow.ToString("o") +
                      "\",\"kind\":\"chat.input\",\"tick\":" + tick.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      ",\"tickSource\":\"native-kernel/tickFrame\",\"pid\":" +
                      Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      ",\"accountId\":\"" + accountId + "\"}\n";
        File.AppendAllText(path, line);
    }
}
