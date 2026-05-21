using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NetVigil.Server.Hubs
{
    [Authorize]
    public class DashboardHub : Hub
    {
    }
}
