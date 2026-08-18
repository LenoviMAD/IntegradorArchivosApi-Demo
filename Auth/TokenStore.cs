using System.Collections.Concurrent;

namespace IntegradorArchivosApi.Demo.Auth;

/// <summary>
/// Token store en memoria: token (64-char hex) → (EmpresaID, expiración).
/// Mismo patrón que el proyecto real: sin persistencia, se limpia pasivamente
/// al validar un token vencido.
/// </summary>
public static class TokenStore
{
    public static readonly ConcurrentDictionary<string, (int EmpresaID, DateTime ExpiresAt)> Tokens = new();

    public static int? ValidateBearer(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("Authorization", out var authHeader))
            return null;

        string header = authHeader.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        string token = header["Bearer ".Length..].Trim();
        if (!Tokens.TryGetValue(token, out var entry))
            return null;

        if (entry.ExpiresAt < DateTime.UtcNow)
        {
            Tokens.TryRemove(token, out _);
            return null;
        }

        return entry.EmpresaID;
    }
}
