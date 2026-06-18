using System.Threading.Channels;

namespace SuperTrigger.Web.Services;

public class TriggerReloadChannel
{
    private readonly List<Channel<bool>> _subscribers = [];
    private readonly object _lock = new();

    public ChannelReader<bool> Subscribe()
    {
        var ch = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock) { _subscribers.Add(ch); }
        return ch.Reader;
    }

    public void RequestReload()
    {
        lock (_lock)
        {
            foreach (var ch in _subscribers)
                ch.Writer.TryWrite(true);
        }
    }
}
