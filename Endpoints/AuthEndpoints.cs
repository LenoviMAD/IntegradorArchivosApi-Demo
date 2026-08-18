using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using IntegradorArchivosApi.Demo.Auth;
using Microsoft.Data.SqlClient;

namespace IntegradorArchivosApi.Demo.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app, string connStr)
    {
        // POST /api/auth/login
        // Mismo patrón que el sistema real: hash SHA-256 de la clave, comparación
        // directa contra la tabla Usuarios con SqlCommand, token random de 32 bytes
        // con expiración de 24hs guardado en memoria (TokenStore).
        app.MapPost("/api/auth/login", async (HttpRequest request) =>
        {
            LoginRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync<LoginRequest>();
            }
            catch
            {
                return Results.BadRequest("JSON inválido.");
            }

            if (body == null || string.IsNullOrWhiteSpace(body.Usuario) || string.IsNullOrWhiteSpace(body.Clave))
                return Results.BadRequest("Usuario y clave son requeridos.");

            try
            {
                byte[] claveBytes = Encoding.UTF8.GetBytes(body.Clave);
                byte[] hashBytes = SHA256.HashData(claveBytes);
                string claveHash = Convert.ToHexString(hashBytes); // uppercase, 64 chars

                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT EmpresaID
                    FROM   Usuarios
                    WHERE  Usuario   = @Usuario
                      AND  ClaveHash = @ClaveHash
                      AND  Baja      = 0";

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Usuario", body.Usuario.Trim());
                cmd.Parameters.AddWithValue("@ClaveHash", claveHash);

                object? scalar = await cmd.ExecuteScalarAsync();

                if (scalar == null || scalar == DBNull.Value)
                    return Results.Unauthorized();

                int empresaID = Convert.ToInt32(scalar);

                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                TokenStore.Tokens[token] = (empresaID, DateTime.UtcNow.AddHours(24));

                return Results.Ok(new { empresaID, token });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error de autenticación: {ex.Message}");
            }
        })
        .WithName("Login")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);
    }
}

public sealed record LoginRequest(
    [property: JsonPropertyName("usuario")] string Usuario,
    [property: JsonPropertyName("clave")] string Clave
);
