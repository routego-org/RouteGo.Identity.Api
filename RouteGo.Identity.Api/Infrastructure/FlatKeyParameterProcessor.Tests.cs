using Amazon.SimpleSystemsManagement.Model;
using RouteGo.Identity.Api.Infrastructure;

namespace RouteGo.Identity.Api.Tests.Infrastructure;

public sealed class FlatKeyParameterProcessorTests
{
    private const string ParameterPath = "/routego/prod/identity/";

    [Fact]
    public void GetKey_KebabCaseName_ReturnsPascalCaseKey()
    {
        // Arrange
        var processor = new FlatKeyParameterProcessor();
        var parameter = new Parameter { Name = "/routego/prod/identity/access-token-minutes" };

        // Act
        string key = processor.GetKey(parameter, ParameterPath);

        // Assert
        Assert.Equal("AccessTokenMinutes", key);
    }

    [Fact]
    public void GetKey_SingleWordName_ReturnsCapitalisedKey()
    {
        // Arrange
        var processor = new FlatKeyParameterProcessor();
        var parameter = new Parameter { Name = "/routego/prod/identity/issuer" };

        // Act
        string key = processor.GetKey(parameter, ParameterPath);

        // Assert
        Assert.Equal("Issuer", key);
    }
}
