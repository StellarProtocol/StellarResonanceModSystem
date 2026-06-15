using System;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

internal sealed class ReflectionGameTypeRegistry : IGameTypeRegistry
{
    public Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }
        return null;
    }
}
