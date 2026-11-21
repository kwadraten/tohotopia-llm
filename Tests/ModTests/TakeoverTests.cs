using Xunit;

[Collection("Loader")]
public class TakeoverTests
{
    [Fact]
    public void Model_takeover_lifecycle() => TakeoverChecks.Run(Assert.True);
}
