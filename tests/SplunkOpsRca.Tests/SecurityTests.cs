using SplunkOpsRca.Infrastructure.Security;

namespace SplunkOpsRca.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void Masks_Secrets_Tokens_ConnectionStrings_And_Pii()
    {
        var service = new SensitiveDataMaskingService();

        var masked = service.Mask("Authorization: Bearer abc.def.ghi password=secret123 email user@example.com Server=db;User Id=app;Password=pwd; accountNumber=123456789012 card=4111 1111 1111 1111 PAN ABCDE1234F");

        Assert.DoesNotContain("secret123", masked);
        Assert.DoesNotContain("user@example.com", masked);
        Assert.DoesNotContain("4111 1111 1111 1111", masked);
        Assert.DoesNotContain("ABCDE1234F", masked);
        Assert.Contains("[MASKED", masked);
    }
}
