using Microsoft.AspNetCore.Http;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class SafeModeMiddlewareTest
{
    [Theory]
    [InlineData("/api/programs", true)]
    [InlineData("/API/PROGRAMS/", true)]
    [InlineData("/api/programs/tags", false)]
    [InlineData("/api/programs-extra", false)]
    public void ExactPathOnlyMatchesItself(string path, bool expected)
    {
        Assert.Equal(expected, SafeModeMiddleware.MatchesAny(["/api/programs"], new PathString(path), null));
    }

    [Theory]
    [InlineData("/api/programs", true)]
    [InlineData("/api/programs/", true)]
    [InlineData("/api/programs/tags", true)]
    [InlineData("/API/PROGRAMS/TAGS/", true)]
    [InlineData("/api/programs/tags/example/launch", true)]
    [InlineData("/api/programs-extra/tags", false)]
    [InlineData("/other/api/programs/tags", false)]
    public void WildcardMatchesSelfAndDescendantsAtPathBoundary(string path, bool expected)
    {
        Assert.Equal(expected, SafeModeMiddleware.MatchesAny(["/api/programs/*"], new PathString(path), null));
    }

    [Theory]
    [InlineData("/api/tasks/example", "/api/tasks/{name}", true)]
    [InlineData("/api/tasks/example/", "/API/TASKS/{NAME}/", true)]
    [InlineData("/api/tasks/example", null, false)]
    [InlineData("/api/tasks/example/run", "/api/tasks/{name}/run", false)]
    public void ParameterPatternMatchesEndpointTemplate(string path, string? route, bool expected)
    {
        Assert.Equal(expected, SafeModeMiddleware.MatchesAny(["/api/tasks/{name}"], new PathString(path), route));
    }

    [Theory]
    [InlineData("/api/programs/tags", true)]
    [InlineData("/api/programs/tags/", true)]
    [InlineData("/api/programs/tags/example", false)]
    [InlineData("/api/programs/users", false)]
    public void ExactExceptionDoesNotAllowDescendants(string path, bool expected)
    {
        Assert.True(SafeModeMiddleware.MatchesAny(["/api/programs/*"], new PathString(path), null));
        Assert.Equal(expected, SafeModeMiddleware.MatchesAny(["/api/programs/tags"], new PathString(path), null));
    }

    [Theory]
    [InlineData("/api/programs/tags", true)]
    [InlineData("/api/programs/tags/", true)]
    [InlineData("/api/programs/tags/example", true)]
    [InlineData("/api/programs/tags/example/launch", true)]
    [InlineData("/api/programs/tags-extra/example", false)]
    public void WildcardExceptionAllowsSelfAndDescendants(string path, bool expected)
    {
        Assert.True(SafeModeMiddleware.MatchesAny(["/api/programs/*"], new PathString(path), null));
        Assert.Equal(expected, SafeModeMiddleware.MatchesAny(["/api/programs/tags/*"], new PathString(path), null));
    }

    [Fact]
    public void ParameterExceptionUsesTheSameTemplateMatching()
    {
        var path = new PathString("/api/tasks/example");

        Assert.True(SafeModeMiddleware.MatchesAny(["/api/tasks/{name}"], path, "/api/tasks/{name}"));
        Assert.False(SafeModeMiddleware.MatchesAny(["/api/tasks/{name}"], path, null));
    }

    [Fact]
    public void LiteralPathDoesNotDependOnEndpointTemplate()
    {
        Assert.True(SafeModeMiddleware.MatchesAny(["/api/programs/tags"], new PathString("/api/programs/tags"), "/api/{module}/{resource}"));
        Assert.False(SafeModeMiddleware.MatchesAny(["/api/programs/tags"], new PathString("/other"), "/api/programs/tags"));
    }
}
