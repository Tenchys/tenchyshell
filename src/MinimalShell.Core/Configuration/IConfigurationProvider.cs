namespace MinimalShell.Core.Configuration;

public interface IConfigurationProvider
{
    ConfigurationLoadResult Load(string? path = null);
}
