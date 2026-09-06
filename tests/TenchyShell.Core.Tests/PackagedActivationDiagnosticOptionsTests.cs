using TenchyShell.Core.Applications;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class PackagedActivationDiagnosticOptionsTests
{
    [Fact]
    public void ParsesMultipleAumidsWithBothContextsByDefault()
    {
        var parsed = PackagedActivationDiagnosticOptions.TryParse(
            ["--diagnose-packaged", "Contoso.App_123!Main", "--diagnose-packaged", "Fabrikam.App_456!App"],
            out var options,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal(PackagedApplicationActivationContextSelection.Both, options.ContextSelection);
        Assert.Equal(["Contoso.App_123!Main", "Fabrikam.App_456!App"], options.AppUserModelIds);
        Assert.Equal(
            [PackagedApplicationActivationContext.InProcess, PackagedApplicationActivationContext.LocalServer],
            options.Contexts);
    }

    [Theory]
    [InlineData("in-process", PackagedApplicationActivationContextSelection.InProcess)]
    [InlineData("local-server", PackagedApplicationActivationContextSelection.LocalServer)]
    [InlineData("both", PackagedApplicationActivationContextSelection.Both)]
    public void ParsesExplicitContext(string value, PackagedApplicationActivationContextSelection expected)
    {
        var parsed = PackagedActivationDiagnosticOptions.TryParse(
            ["--diagnose-packaged", "Contoso.App_123!Main", "--activation-context", value],
            out var options,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal(expected, options.ContextSelection);
    }

    [Fact]
    public void RejectsAmbiguousDiagnosticArguments()
    {
        AssertRejected(["--diagnose-packaged"]);
        AssertRejected(["--diagnose-packaged", "--activation-context", "both"]);
        AssertRejected(["--diagnose-packaged", "Contoso!App", "--activation-context", "invalid"]);
        AssertRejected(["--activation-context", "both"]);
    }

    private static void AssertRejected(string[] args)
    {
        var parsed = PackagedActivationDiagnosticOptions.TryParse(args, out _, out var error);

        Assert.False(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RejectsRepeatedActivationContext()
    {
        var parsed = PackagedActivationDiagnosticOptions.TryParse(
            ["--diagnose-packaged", "Contoso!App", "--activation-context", "in-process", "--activation-context", "local-server"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Contains("no puede repetirse", error, StringComparison.OrdinalIgnoreCase);
    }
}
