using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Core;

/// <summary>
/// A self-contained unit of application composition: one feature area's service registrations,
/// added to the builder with <see cref="ShenoraApplicationBuilder.AddModule"/> and applied at
/// <see cref="ShenoraApplicationBuilder.Build"/> in registration order.
///
/// This is the generalization of the family's per-module <c>AddXxxServices</c> extension methods —
/// the proven granularity for slicing an app. Registration-time context is deliberately NOT passed
/// in: the builder registers <see cref="ShenoraEnvironment"/> and <see cref="ShenoraPaths"/> as
/// services, so anything a module's services need is resolved from DI (factory lambdas for
/// construction-time values), keeping modules order-independent. IPC route mapping and richer
/// lifecycle participation land on top of this in later phases.
/// </summary>
public interface IShenoraModule
{
    /// <summary>Register this module's services. Called once, during <c>Build()</c>.</summary>
    void ConfigureServices(IServiceCollection services);
}
