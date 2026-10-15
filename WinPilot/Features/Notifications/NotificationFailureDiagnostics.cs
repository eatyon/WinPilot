using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using WinPilot.Core;

namespace WinPilot.Features.Notifications;

internal static class NotificationFailureDiagnostics
{
    private static readonly Regex Url = new(@"\b(?:https?|wss?|socks5):\\?/\\?/[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Credential = new(@"\b(token|access_token|secret|client_secret|password|authorization)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Authorization = new(@"\b(Bearer|Basic|QQBot)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string[] Secrets(NotificationSettings settings, CommandBotSettings? bots = null)
    {
        var values = new List<string> { settings.TelegramBotToken, settings.OneBotToken, settings.FeishuAppSecret, settings.QQBotClientSecret, bots?.OneBotToken ?? "" };
        foreach (var url in new[] { settings.WebhookUrl, settings.WorkWeixinWebhookUrl, settings.FeishuWebhookUrl,
            settings.OneBotEndpoint, settings.TelegramApiBaseUrl, settings.TelegramProxyUrl, bots?.OneBotWebSocketUrl ?? "" })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address)) continue;
            values.Add(url);
            if (address.UserInfo.Length > 0) values.AddRange(address.UserInfo.Split(':').Select(Uri.UnescapeDataString));
            foreach (var pair in address.Query.TrimStart('?').Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && new[] { "key", "token", "access_token", "secret", "password" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                    values.Add(Uri.UnescapeDataString(parts[1]));
            }
        }
        return values.ToArray();
    }

    internal static string Format(Exception exception, IEnumerable<string>? secrets = null)
    {
        var known = (secrets ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct()
            .SelectMany(value => new[] { value, Uri.EscapeDataString(value) }).OrderByDescending(value => value.Length).ToArray();
        string Redact(string text)
        {
            text = Url.Replace(text, "[URL]");
            text = Authorization.Replace(text, "$1 [REDACTED]");
            text = Credential.Replace(text, "$1=[REDACTED]");
            foreach (var value in known) text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            return text;
        }
        var result = new StringBuilder();
        void Append(Exception error, int depth)
        {
            result.Append(' ', depth * 2).Append(error.GetType().FullName).Append(": ").AppendLine(Redact(error.Message));
            result.Append(' ', depth * 2).Append("HResult: ").AppendLine(error.HResult.ToString("X8"));
            if (error is HttpRequestException { StatusCode: { } status }) result.Append("HTTP: ").AppendLine(((int)status).ToString());
            if (error is Win32Exception native) result.Append("NativeErrorCode: ").AppendLine(native.NativeErrorCode.ToString());
            if (error is QQBotGatewayClosedException closed) result.Append("GatewayCloseCode: ").AppendLine(closed.Code?.ToString() ?? "unknown");
            if (error.StackTrace is { } stack) result.AppendLine(Redact(stack));
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) Append(inner, depth + 1);
            else if (error.InnerException is { } inner) Append(inner, depth + 1);
        }
        Append(exception, 0);
        return result.ToString();
    }

    internal static Exception Redact(Exception exception, IEnumerable<string>? secrets = null) => new DiagnosticException(Format(exception, secrets));

    private sealed class DiagnosticException(string diagnostics) : Exception(diagnostics)
    {
        public override string ToString() => Message;
    }
}
