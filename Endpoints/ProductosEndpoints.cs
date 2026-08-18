using IntegradorArchivosApi.Demo.Auth;
using Microsoft.Data.SqlClient;

namespace IntegradorArchivosApi.Demo.Endpoints;

public static class ProductosEndpoints
{
    public static void MapProductosEndpoints(this WebApplication app, string connStr)
    {
        // GET /api/productos — protegido, SQL directo contra la tabla Productos.
        app.MapGet("/api/productos", async (HttpRequest request) =>
        {
            int? empresaId = TokenStore.ValidateBearer(request);
            if (empresaId is null)
                return Results.Unauthorized();

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT Id, Codigo, Nombre, Precio, Stock, EmpresaID
                    FROM   Productos
                    WHERE  EmpresaID = @EmpresaID
                    ORDER BY Codigo";

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@EmpresaID", empresaId.Value);

                var productos = new List<object>();
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    productos.Add(new
                    {
                        Id = rdr.GetInt32(rdr.GetOrdinal("Id")),
                        Codigo = rdr.GetString(rdr.GetOrdinal("Codigo")),
                        Nombre = rdr.GetString(rdr.GetOrdinal("Nombre")),
                        Precio = rdr.GetDecimal(rdr.GetOrdinal("Precio")),
                        Stock = rdr.GetInt32(rdr.GetOrdinal("Stock")),
                        EmpresaID = rdr.GetInt32(rdr.GetOrdinal("EmpresaID")),
                    });
                }

                return Results.Ok(productos);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error leyendo productos: {ex.Message}");
            }
        })
        .WithName("GetProductos")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // POST /api/productos — protegido, INSERT directo.
        app.MapPost("/api/productos", async (HttpRequest request) =>
        {
            int? empresaId = TokenStore.ValidateBearer(request);
            if (empresaId is null)
                return Results.Unauthorized();

            NuevoProductoRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync<NuevoProductoRequest>();
            }
            catch
            {
                return Results.BadRequest("JSON inválido.");
            }

            if (body == null || string.IsNullOrWhiteSpace(body.Codigo) || string.IsNullOrWhiteSpace(body.Nombre))
                return Results.BadRequest("Codigo y Nombre son requeridos.");

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO Productos (Codigo, Nombre, Precio, Stock, EmpresaID)
                    OUTPUT inserted.Id
                    VALUES (@Codigo, @Nombre, @Precio, @Stock, @EmpresaID)";

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Codigo", body.Codigo.Trim());
                cmd.Parameters.AddWithValue("@Nombre", body.Nombre.Trim());
                cmd.Parameters.AddWithValue("@Precio", body.Precio);
                cmd.Parameters.AddWithValue("@Stock", body.Stock);
                cmd.Parameters.AddWithValue("@EmpresaID", empresaId.Value);

                object? scalar = await cmd.ExecuteScalarAsync();
                int nuevoId = Convert.ToInt32(scalar);

                return Results.Created($"/api/productos/{nuevoId}", new { Id = nuevoId });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error insertando producto: {ex.Message}");
            }
        })
        .WithName("PostProducto")
        .Accepts<NuevoProductoRequest>("application/json")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);
    }
}

public sealed record NuevoProductoRequest(string Codigo, string Nombre, decimal Precio, int Stock);
