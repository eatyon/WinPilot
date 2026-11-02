using WinPilot.Features.Security;

namespace WinPilot.Web;

internal static class SessionCookie
{
    public const string Name = "WinPilot.Session";

    public static void Append(HttpContext context, string session)
    {
        context.Response.Cookies.Append(Name, session, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            MaxAge = SessionManager.Lifetime
        });
    }
}
