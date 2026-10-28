using System.Security.Cryptography;
using System.Text;
using Vusos.Core.Domain;
using Vusos.Core.Services.Json;

namespace Vusos.Core.Services;

/// <summary>Klucze cache: <c>{userScope}:{system}:{method}:{argsHash}</c>.</summary>
public static class CacheKey
{
    public static string Create(string userScope, SourceSystem system, string method, params object?[] args) =>
        $"{MethodPrefix(userScope, system, method)}{HashArgs(args)}";

    /// <summary>Wszystkie klucze danego systemu: <c>{userScope}:{system}:</c>.</summary>
    public static string SystemPrefix(string userScope, SourceSystem system) => $"{userScope}:{system}:";

    /// <summary>Wszystkie klucze danej metody (dla dowolnych argumentów).</summary>
    public static string MethodPrefix(string userScope, SourceSystem system, string method) =>
        $"{SystemPrefix(userScope, system)}{method}:";

    /// <summary>SHA-256 z JSON-a argumentów (32 znaki hex).</summary>
    public static string HashArgs(params object?[] args)
    {
        var json = args.Length == 0 ? "[]" : System.Text.Json.JsonSerializer.Serialize(args, VusosJson.Options);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
