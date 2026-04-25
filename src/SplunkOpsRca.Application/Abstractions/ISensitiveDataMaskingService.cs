namespace SplunkOpsRca.Application.Abstractions;

public interface ISensitiveDataMaskingService
{
    string Mask(string input);
}
