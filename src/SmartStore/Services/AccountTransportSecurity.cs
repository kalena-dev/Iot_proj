using System.Net;

namespace SmartStore.Services;

public static class AccountTransportSecurity
{
    public static bool Allows(HttpContext context)
    {
        if (context.Request.IsHttps) return true;
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            !HttpMethods.IsGet(context.Request.Method)) return false;
        var path = context.Request.Path;
        return path == "/health" || path.StartsWithSegments("/css") ||
            path.StartsWithSegments("/js") || path.StartsWithSegments("/lib");
    }
}
