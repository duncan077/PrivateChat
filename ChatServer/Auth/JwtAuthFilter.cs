using System.Security.Claims;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;

namespace ChatServer.Auth;

public class JwtAuthFilter : MagicOnionFilterAttribute
{
    public override async ValueTask Invoke(ServiceContext context, Func<ServiceContext, ValueTask> next)
    {
        var httpContext = context.CallContext.GetHttpContext();
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.Unauthenticated, "Authentication required");
        }
        await next(context);
    }
}

public static class ServiceContextExtensions
{
    public static Guid GetUserId(this ServiceContext context)
    {
        var httpContext = context.CallContext.GetHttpContext();
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null ? Guid.Parse(claim.Value) : throw new ReturnStatusException(Grpc.Core.StatusCode.Unauthenticated, "User not found");
    }

    public static string GetUserName(this ServiceContext context)
    {
        var httpContext = context.CallContext.GetHttpContext();
        return httpContext.User.FindFirst(ClaimTypes.Name)?.Value
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.Unauthenticated, "User not found");
    }
}
