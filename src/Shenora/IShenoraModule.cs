using Microsoft.Extensions.DependencyInjection;
using Shenora.Core.Ipc;

namespace Shenora;

/// <summary>
/// A self-contained unit of application composition: one feature area's service registrations,
/// added to the builder with <see cref="ShenoraApplicationBuilder.AddModule"/> and applied at
/// <see cref="ShenoraApplicationBuilder.Build"/> in registration order.
///
/// This is the generalization of the family's per-module <c>AddXxxServices</c> extension methods —
/// the proven granularity for slicing an app. Registration-time context is deliberately NOT passed
/// in: the builder registers <see cref="ShenoraEnvironment"/> and <see cref="ShenoraPaths"/> as
/// services, so anything a module's services need is resolved from DI (factory lambdas for
/// construction-time values), keeping modules order-independent.
/// <para>
/// IPC route mapping and lifecycle participation build ON this rather than widening it: a module
/// registers its <c>IIpcModule</c> here (via <c>UseIpcModule</c>) and the dispatcher maps the
/// registered facades itself, while anything needing the live window is mapped LATE, from where the
/// window is created. So this interface stays at one member deliberately — it is not a placeholder
/// awaiting more.
/// </para>
/// </summary>
public interface IShenoraModule
{
    /// <summary>Register this module's services. Called once, during <c>Build()</c>.</summary>
    void ConfigureServices(IServiceCollection services);
}
