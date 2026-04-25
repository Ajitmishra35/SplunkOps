using System.Text.RegularExpressions;
using SplunkOpsRca.Application.Abstractions;

namespace SplunkOpsRca.Infrastructure.Security;

public sealed partial class SensitiveDataMaskingService : ISensitiveDataMaskingService
{
    public string Mask(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var value = input;
        value = AuthorizationHeaderRegex().Replace(value, "$1 [MASKED_AUTHORIZATION]");
        value = JwtRegex().Replace(value, "[MASKED_JWT]");
        value = SecretKeyValueRegex().Replace(value, "$1[MASKED_SECRET]");
        value = ConnectionStringRegex().Replace(value, "$1[MASKED_CONNECTION_STRING]");
        value = EmailRegex().Replace(value, "[MASKED_EMAIL]");
        value = PanRegex().Replace(value, "[MASKED_PAN]");
        value = AadhaarRegex().Replace(value, "[MASKED_AADHAAR]");
        value = CardRegex().Replace(value, "[MASKED_CARD]");
        value = AccountRegex().Replace(value, "$1[MASKED_ACCOUNT]");
        return value;
    }

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*)(bearer\s+)?[A-Za-z0-9._\-+/=]+")]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|access_token|refresh_token|client_secret|api[-_ ]?key|x-api-key|secret)\b\s*[:=]\s*[""']?[^""'\s,;}&]+")]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(@"(?i)\b((server|data source|host|user id|uid|password|pwd)\s*=\s*[^;]+;){2,}[^""'\s]*")]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b[A-Z]{5}[0-9]{4}[A-Z]\b")]
    private static partial Regex PanRegex();

    [GeneratedRegex(@"\b\d{4}\s?\d{4}\s?\d{4}\b")]
    private static partial Regex AadhaarRegex();

    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b")]
    private static partial Regex CardRegex();

    [GeneratedRegex(@"(?i)\b(account(number|no)?|acct)\s*[:=]\s*[A-Za-z0-9\-]{6,}")]
    private static partial Regex AccountRegex();
}
