using System;
using System.Linq;

namespace Stellar.Application.Tests;

/// <summary>Counts an interface's declared members the way STELLAR0005 does (methods excl. accessors + properties + events).</summary>
internal static class ContractMembers
{
    public static int Count(Type t) =>
        t.GetMethods().Count(m => !m.IsSpecialName) + t.GetProperties().Length + t.GetEvents().Length;
}
