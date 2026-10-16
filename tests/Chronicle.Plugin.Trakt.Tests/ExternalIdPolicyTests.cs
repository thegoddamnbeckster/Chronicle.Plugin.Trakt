using Chronicle.Plugin.Trakt;
using Xunit;

namespace Chronicle.Plugin.Trakt.Tests;

public class ExternalIdPolicyTests
{
    [Fact]
    public void TheProviderDeclaresHowItsIdsBehave()
    {
        var p = new TraktMetadataProvider().GetExternalIdPolicy();
        Assert.Equal("trakt", p.Source);
        Assert.Equal("trakt:movie:{id}", p.CrossRefFormats["movie"]);
        Assert.Equal("trakt:show:{id}", p.CrossRefFormats["default"]);
        Assert.Matches(p.FamilyIdPatterns["movie"], "trakt:movie:348356");
    }
}
