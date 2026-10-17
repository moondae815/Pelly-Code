using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace PellyCoder.Cli;

/// <summary>Bridges Spectre.Console.Cli's type registration to Microsoft.Extensions.DependencyInjection.</summary>
public sealed class DiTypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build() => new Resolver(services.BuildServiceProvider());

    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());

    private sealed class Resolver(ServiceProvider provider) : ITypeResolver, IDisposable
    {
        public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);

        public void Dispose() => provider.Dispose();
    }
}
