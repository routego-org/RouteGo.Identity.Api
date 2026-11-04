using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RouteGo.Identity.Api.Features.Authentication.PublishIdentityEvents;
using RouteGo.Identity.Api.Tests.Database;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>
/// The real host over the test database, without the five-second background publisher: an HTTP
/// test asserts on the events a request recorded, and a publisher running underneath it would
/// race the assertion. The publisher's own tests drive its handler directly.
/// </summary>
internal static class IdentityTestHost
{
    public static ApiFactory For(DatabaseFixture database, params Action<IServiceCollection>[] overrides)
    {
        var all = new List<Action<IServiceCollection>> { RemoveBackgroundPublisher };
        all.AddRange(overrides);
        return TestHost.For(database, all.ToArray());
    }

    private static void RemoveBackgroundPublisher(IServiceCollection services)
    {
        ServiceDescriptor? publisher = services.SingleOrDefault(IsBackgroundPublisher);
        if (publisher != null)
        {
            services.Remove(publisher);
        }
    }

    private static bool IsBackgroundPublisher(ServiceDescriptor descriptor)
    {
        return descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(PublishIdentityEventsService);
    }
}
