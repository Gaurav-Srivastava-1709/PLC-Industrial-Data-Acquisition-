using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Text.RegularExpressions;

namespace WTT.UI.Logging;

/// <summary>
/// Masks sensitive data (PII, secrets) in log events.
/// Runs after other enrichers, before sinks write.
/// Patterns covered:
/// - Passwords in connection strings: Password=xxx; Pwd=xxx;
/// - Authorization headers: Bearer xxx, Basic xxx
/// - JSON fields: "password": "xxx", "token": "xxx", "secret": "xxx"
/// - SQL connection strings: User ID=xxx; Password=xxx;
/// - URLs with credentials: user:pass@host
/// </summary>
public sealed class PiiMaskingEnricher : ILogEventEnricher
{
    private static readonly Regex[] Patterns =
    {
        // Connection string passwords: Password=xxx; Pwd=xxx;
        new Regex(@"(password|pwd)\s*=\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Authorization headers: Authorization: Bearer xxx, Basic xxx
        new Regex(@"(authorization\s*:\s*)(bearer\s+)([^\s,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(authorization\s*:\s*)(basic\s+)([^\s,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // JSON fields: "password": "xxx", "token": "xxx", "secret": "xxx", "apikey": "xxx", "access_token": "xxx"
        new Regex(@"(""password""|""pwd""|""token""|""secret""|""apikey""|""access_token""|""refresh_token""|""client_secret"")\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // URLs with credentials: https://user:pass@host
        new Regex(@"(https?://)([^:]+):([^@]+)@", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Connection string User ID / Password
        new Regex(@"(user\s*id|uid)\s*=\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // API keys in headers: x-api-key: xxx, api-key: xxx
        new Regex(@"(x-api-key|api-key)\s*:\s*([^\s,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private const string Mask = "***MASKED***";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Process message template and properties
        foreach (var property in logEvent.Properties)
        {
            if (property.Value is ScalarValue sv && sv.Value is string str)
            {
                var masked = MaskSensitiveData(str);
                if (masked != str)
                {
                    // Replace the property with masked value
                    logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(masked)));
                }
            }
        }

        // Also check the message template itself
        if (!string.IsNullOrEmpty(logEvent.MessageTemplate.Text))
        {
            var maskedTemplate = MaskSensitiveData(logEvent.MessageTemplate.Text);
            if (maskedTemplate != logEvent.MessageTemplate.Text)
            {
                // Note: MessageTemplate is immutable in Serilog, so we can't easily change it.
                // The property masking above handles most cases.
            }
        }
    }

    private static string MaskSensitiveData(string input)
    {
        var result = input;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, m =>
            {
                // Preserve the key/prefix, mask the value
                if (m.Groups.Count >= 3)
                {
                    return $"{m.Groups[1].Value}{Mask}";
                }
                else if (m.Groups.Count == 2)
                {
                    return $"{m.Groups[1].Value}{Mask}";
                }
                return Mask;
            });
        }
        return result;
    }
}

/// <summary>
/// Extension to easily add PII masking to logger configuration.
/// </summary>
public static class PiiMaskingEnricherExtensions
{
    public static LoggerConfiguration WithPiiMasking(this LoggerConfiguration enrich)
    {
        return enrich.Enrich.With(new PiiMaskingEnricher());
    }
}