using Microsoft.AspNetCore.SignalR;

namespace Analytika.Controllers;

public class JarvisHub : Hub
{
    public async Task SubscribeToMetrics()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "jarvis-metrics");
    }

    public async Task UnsubscribeFromMetrics()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "jarvis-metrics");
    }

    public async Task SubscribeToFacility(int facilityId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"jarvis-facility-{facilityId}");
    }

    public async Task UnsubscribeFromFacility(int facilityId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"jarvis-facility-{facilityId}");
    }

    public async Task SubscribeToActivityStream()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "jarvis-activity");
    }

    public async Task UnsubscribeFromActivityStream()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "jarvis-activity");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "jarvis-metrics");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "jarvis-activity");
        await base.OnDisconnectedAsync(exception);
    }
}