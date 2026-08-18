using System.Data;
using System.Text.Json;
using IntegradorArchivosApi.Demo.Auth;
using Microsoft.Data.SqlClient;

namespace IntegradorArchivosApi.Demo.Endpoints;

public static class PedidosEndpoints
{
    public static void MapPedidosEndpoints(this WebApplication app, string connStr)
    {
        // POST /api/pedidos — protegido, llama a sp_RegistrarPedido (CommandType.StoredProcedure).
        app.MapPost("/api/pedidos", async (HttpRequest request) =>
        {
            int? empresaId = TokenStore.ValidateBearer(request);
            if (empresaId is null)
                return Results.Unauthorized();

            RegistrarPedidoRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync<RegistrarPedidoRequest>();
            }
            catch (Exception ex)
            {
                return Results.BadRequest($"JSON inválido: {ex.Message}");
            }

            if (body == null || body.Items == null || body.Items.Count == 0)
                return Results.BadRequest("El pedido no tiene líneas de detalle.");

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                string detalleJson = JsonSerializer.Serialize(body.Items);

                await using var cmd = new SqlCommand("sp_RegistrarPedido", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };
                cmd.Parameters.AddWithValue("@EmpresaID", empresaId.Value);
                cmd.Parameters.AddWithValue("@DetalleJson", detalleJson);
                var outParam = new SqlParameter("@PedidosID", SqlDbType.Int) { Direction = ParameterDirection.Output };
                cmd.Parameters.Add(outParam);

                await cmd.ExecuteNonQueryAsync();

                int pedidosId = (int)outParam.Value;

                return Results.Created($"/api/pedidos/{pedidosId}", new { PedidosID = pedidosId });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error registrando pedido: {ex.Message}");
            }
        })
        .WithName("RegistrarPedido")
        .Accepts<RegistrarPedidoRequest>("application/json")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // GET /api/pedidos/pendientes/{empresaId} — protegido, SELECT directo con join a PedidosDetalle.
        app.MapGet("/api/pedidos/pendientes/{empresaId:int}", async (int empresaId, HttpRequest request) =>
        {
            int? tokenEmpresaId = TokenStore.ValidateBearer(request);
            if (tokenEmpresaId is null)
                return Results.Unauthorized();
            if (tokenEmpresaId != empresaId)
                return Results.Forbid();

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT p.Id AS PedidoId, p.Fecha, p.Estado,
                           d.Id AS DetalleId, d.ProductoId, d.Cantidad,
                           pr.Codigo, pr.Nombre
                    FROM   Pedidos p
                    JOIN   PedidosDetalle d ON d.PedidoId = p.Id
                    JOIN   Productos pr     ON pr.Id = d.ProductoId
                    WHERE  p.EmpresaID = @EmpresaID
                      AND  p.Estado    = 'Pendiente'
                    ORDER BY p.Id, d.Id";

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@EmpresaID", empresaId);

                var pedidos = new Dictionary<int, PedidoPendienteDto>();
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    int pedidoId = rdr.GetInt32(rdr.GetOrdinal("PedidoId"));
                    if (!pedidos.TryGetValue(pedidoId, out var pedido))
                    {
                        pedido = new PedidoPendienteDto(
                            pedidoId,
                            rdr.GetDateTime(rdr.GetOrdinal("Fecha")),
                            rdr.GetString(rdr.GetOrdinal("Estado")),
                            new List<PedidoDetalleDto>());
                        pedidos[pedidoId] = pedido;
                    }

                    pedido.Detalle.Add(new PedidoDetalleDto(
                        rdr.GetInt32(rdr.GetOrdinal("DetalleId")),
                        rdr.GetInt32(rdr.GetOrdinal("ProductoId")),
                        rdr.GetString(rdr.GetOrdinal("Codigo")),
                        rdr.GetString(rdr.GetOrdinal("Nombre")),
                        rdr.GetInt32(rdr.GetOrdinal("Cantidad"))));
                }

                if (pedidos.Count == 0)
                    return Results.NotFound("No hay pedidos pendientes.");

                return Results.Ok(pedidos.Values);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error obteniendo pedidos pendientes: {ex.Message}");
            }
        })
        .WithName("GetPedidosPendientes")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);
    }
}

public sealed record RegistrarPedidoRequest(List<PedidoItemRequest> Items);

public sealed record PedidoItemRequest(int ProductoId, int Cantidad);

public sealed record PedidoDetalleDto(int DetalleId, int ProductoId, string Codigo, string Nombre, int Cantidad);

public sealed record PedidoPendienteDto(int PedidoId, DateTime Fecha, string Estado, List<PedidoDetalleDto> Detalle);
