using IntegradorArchivosApi.Demo.Auth;
using Microsoft.Data.SqlClient;

namespace IntegradorArchivosApi.Demo.Endpoints;

/// <summary>
/// Los 4 endpoints de sincronización masiva que SincroApp-Demo llama al subir
/// CSV/JSON (clientes, productos, stock, maestros). El EmpresaID siempre se
/// toma del token Bearer validado, nunca del body — mismo criterio de
/// aislamiento por empresa que el resto de la API.
/// </summary>
public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app, string connStr)
    {
        // POST /api/sync/clientes — upsert masivo por (EmpresaID, Codigo).
        app.MapPost("/api/sync/clientes", async (HttpRequest request) =>
        {
            int? empresaId = TokenStore.ValidateBearer(request);
            if (empresaId is null)
                return Results.Unauthorized();

            List<ClienteSyncItem>? body;
            try
            {
                body = await request.ReadFromJsonAsync<List<ClienteSyncItem>>();
            }
            catch
            {
                return Results.BadRequest("JSON inválido.");
            }

            if (body == null || body.Count == 0)
                return Results.BadRequest("No se recibieron clientes.");

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                int procesados = 0, invalidos = 0;
                foreach (var c in body)
                {
                    string codigo = (c.Codigo ?? "").Trim();
                    string nombre = (c.Nombre ?? "").Trim();
                    if (codigo.Length == 0 || nombre.Length == 0)
                    {
                        invalidos++;
                        continue;
                    }

                    const string sql = @"
                        MERGE dbo.Clientes AS tgt
                        USING (SELECT @EmpresaID AS EmpresaID, @Codigo AS Codigo) AS src
                            ON tgt.EmpresaID = src.EmpresaID AND tgt.Codigo = src.Codigo
                        WHEN MATCHED THEN UPDATE SET
                            Nombre = @Nombre, Direccion = @Direccion, Localidad = @Localidad,
                            Telefono = @Telefono, DiaDeVenta = @DiaDeVenta,
                            PorcentajePercepcionIB = @PorcentajePercepcionIB,
                            Latitud = @Latitud, Longitud = @Longitud,
                            ListasPreciosID = @ListasPreciosID, TiposDocumentosID = @TiposDocumentosID,
                            NumeroDocumento = @NumeroDocumento, Baja = @Baja,
                            FechaActualizacion = SYSUTCDATETIME()
                        WHEN NOT MATCHED THEN INSERT
                            (EmpresaID, Codigo, Nombre, Direccion, Localidad, Telefono, DiaDeVenta,
                             PorcentajePercepcionIB, Latitud, Longitud, ListasPreciosID,
                             TiposDocumentosID, NumeroDocumento, Baja)
                        VALUES
                            (@EmpresaID, @Codigo, @Nombre, @Direccion, @Localidad, @Telefono, @DiaDeVenta,
                             @PorcentajePercepcionIB, @Latitud, @Longitud, @ListasPreciosID,
                             @TiposDocumentosID, @NumeroDocumento, @Baja);";

                    await using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@EmpresaID", empresaId.Value);
                    cmd.Parameters.AddWithValue("@Codigo", codigo);
                    cmd.Parameters.AddWithValue("@Nombre", nombre);
                    cmd.Parameters.AddWithValue("@Direccion", (object?)c.Direccion?.Trim() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Localidad", (object?)c.Localidad?.Trim() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Telefono", (object?)c.Telefono?.Trim() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DiaDeVenta", c.DiaDeVenta);
                    cmd.Parameters.AddWithValue("@PorcentajePercepcionIB", c.PorcentajePercepcionIB);
                    cmd.Parameters.AddWithValue("@Latitud", c.Latitud != 0 ? c.Latitud : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@Longitud", c.Longitud != 0 ? c.Longitud : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ListasPreciosID", c.ListasPreciosID > 0 ? c.ListasPreciosID : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@TiposDocumentosID", c.TiposDocumentosID > 0 ? c.TiposDocumentosID : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@NumeroDocumento", (object?)c.NumeroDocumento?.Trim() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Baja", c.Baja);

                    await cmd.ExecuteNonQueryAsync();
                    procesados++;
                }

                return Results.Ok(new { total = body.Count, procesados, invalidos });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error sync clientes: {ex.Message}");
            }
        })
        .WithName("SyncClientes")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // POST /api/sync/productos — upsert masivo por (EmpresaID, Codigo).
        app.MapPost("/api/sync/productos", async (HttpRequest request) =>
        {
            int? empresaId = TokenStore.ValidateBearer(request);
            if (empresaId is null)
                return Results.Unauthorized();

            List<ProductoSyncItem>? body;
            try
            {
                body = await request.ReadFromJsonAsync<List<ProductoSyncItem>>();
            }
            catch
            {
                return Results.BadRequest("JSON inválido.");
            }

            if (body == null || body.Count == 0)
                return Results.BadRequest("No se recibieron productos.");

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                int procesados = 0, invalidos = 0;
                foreach (var p in body)
                {
                    string codigo = (p.CodigoDeProducto ?? "").Trim();
                    string nombre = (p.Nombre ?? "").Trim();
                    if (codigo.Length == 0 || nombre.Length == 0 || p.PrecioProducto < 0)
                    {
                        invalidos++;
                        continue;
                    }

                    int? categoriaComercialId = p.CategoriasComercialesIDs is { Count: > 0 }
                        ? p.CategoriasComercialesIDs[0]
                        : null;

                    const string sql = @"
                        MERGE dbo.Productos AS tgt
                        USING (SELECT @EmpresaID AS EmpresaID, @Codigo AS Codigo) AS src
                            ON tgt.EmpresaID = src.EmpresaID AND tgt.Codigo = src.Codigo
                        WHEN MATCHED THEN UPDATE SET
                            Nombre = @Nombre, Precio = @Precio, Stock = @Stock,
                            MarcasProductosID = @MarcasProductosID, FamiliaProductosID = @FamiliaProductosID,
                            ImpuestosID = @ImpuestosID, SubCategoriasProductosID = @SubCategoriasProductosID,
                            CategoriasComercialesID = @CategoriasComercialesID,
                            ProductosIDPadre = @ProductosIDPadre,
                            DesactivadoParaLaVenta = @DesactivadoParaLaVenta,
                            CantidadMultiplo = @CantidadMultiplo, UnidadesPorBulto = @UnidadesPorBulto,
                            StockPropio = @StockPropio, Baja = @Baja
                        WHEN NOT MATCHED THEN INSERT
                            (EmpresaID, Codigo, Nombre, Precio, Stock, MarcasProductosID, FamiliaProductosID,
                             ImpuestosID, SubCategoriasProductosID, CategoriasComercialesID, ProductosIDPadre,
                             DesactivadoParaLaVenta, CantidadMultiplo, UnidadesPorBulto, StockPropio, Baja)
                        VALUES
                            (@EmpresaID, @Codigo, @Nombre, @Precio, @Stock, @MarcasProductosID, @FamiliaProductosID,
                             @ImpuestosID, @SubCategoriasProductosID, @CategoriasComercialesID, @ProductosIDPadre,
                             @DesactivadoParaLaVenta, @CantidadMultiplo, @UnidadesPorBulto, @StockPropio, @Baja);";

                    await using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@EmpresaID", empresaId.Value);
                    cmd.Parameters.AddWithValue("@Codigo", codigo);
                    cmd.Parameters.AddWithValue("@Nombre", nombre);
                    cmd.Parameters.AddWithValue("@Precio", p.PrecioProducto);
                    cmd.Parameters.AddWithValue("@Stock", p.StockEnUnidades);
                    cmd.Parameters.AddWithValue("@MarcasProductosID", p.MarcasProductosID > 0 ? p.MarcasProductosID : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@FamiliaProductosID", p.FamiliaProductosID > 0 ? p.FamiliaProductosID : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ImpuestosID", p.ImpuestosID > 0 ? p.ImpuestosID : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@SubCategoriasProductosID", p.SubCategoriasProductosID > 0 ? p.SubCategoriasProductosID : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@CategoriasComercialesID", categoriaComercialId is > 0 ? categoriaComercialId.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ProductosIDPadre", p.ProductosIDPadre is > 0 ? p.ProductosIDPadre.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@DesactivadoParaLaVenta", p.DesactivadoParaLaVenta);
                    cmd.Parameters.AddWithValue("@CantidadMultiplo", p.CantidadMultiplo > 0 ? p.CantidadMultiplo : 1);
                    cmd.Parameters.AddWithValue("@UnidadesPorBulto", p.UnidadesPorBulto > 0 ? p.UnidadesPorBulto : 1);
                    cmd.Parameters.AddWithValue("@StockPropio", p.StockPropio);
                    cmd.Parameters.AddWithValue("@Baja", p.Baja);

                    await cmd.ExecuteNonQueryAsync();
                    procesados++;
                }

                return Results.Ok(new { total = body.Count, procesados, invalidos });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error sync productos: {ex.Message}");
            }
        })
        .WithName("SyncProductos")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // POST /api/sync/stock — actualiza Stock en Productos por (EmpresaID, Codigo).
        // StockUnidades llega como string (así lo manda el ERP real); hay que parsearlo
        // y validar que sea un entero no negativo antes de aplicar el update.
        app.MapPost("/api/sync/stock", async (HttpRequest request) =>
        {
            int? empresaId = TokenStore.ValidateBearer(request);
            if (empresaId is null)
                return Results.Unauthorized();

            List<StockSyncItem>? body;
            try
            {
                body = await request.ReadFromJsonAsync<List<StockSyncItem>>();
            }
            catch
            {
                return Results.BadRequest("JSON inválido.");
            }

            if (body == null || body.Count == 0)
                return Results.BadRequest("No se recibió stock.");

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                int actualizados = 0, invalidos = 0, noEncontrados = 0;
                foreach (var s in body)
                {
                    string codigo = (s.CodigoProducto ?? "").Trim();
                    bool cantidadValida = int.TryParse(s.StockUnidades, out int cantidad) && cantidad >= 0;

                    if (codigo.Length == 0 || !cantidadValida)
                    {
                        invalidos++;
                        continue;
                    }

                    const string sql = @"
                        UPDATE dbo.Productos
                        SET Stock = @Stock
                        WHERE EmpresaID = @EmpresaID AND Codigo = @Codigo";

                    await using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@Stock", cantidad);
                    cmd.Parameters.AddWithValue("@EmpresaID", empresaId.Value);
                    cmd.Parameters.AddWithValue("@Codigo", codigo);

                    int filas = await cmd.ExecuteNonQueryAsync();
                    if (filas > 0)
                        actualizados++;
                    else
                        noEncontrados++;
                }

                return Results.Ok(new { total = body.Count, actualizados, invalidos, noEncontrados });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error sync stock: {ex.Message}");
            }
        })
        .WithName("SyncStock")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // POST /api/sync/maestros — upsert de los 8 catálogos de referencia.
        app.MapPost("/api/sync/maestros", async (HttpRequest request) =>
        {
            int? empresaId = TokenStore.ValidateBearer(request);
            if (empresaId is null)
                return Results.Unauthorized();

            MaestrosSyncRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync<MaestrosSyncRequest>();
            }
            catch
            {
                return Results.BadRequest("JSON inválido.");
            }

            if (body == null)
                return Results.BadRequest("Body vacío.");

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                var resultado = new Dictionary<string, object>();

                resultado["impuestos"] = await UpsertPorEmpresaAsync(conn, "dbo.Impuestos", "ImpuestosID", empresaId.Value,
                    body.Impuestos.Select(i => (i.ImpuestosID, i.Nombre, extraCols: ", Porcentaje", extraVals: ", @Extra1", (object[])[i.Porcentaje])));

                resultado["marcasProductos"] = await UpsertPorEmpresaAsync(conn, "dbo.MarcasProductos", "MarcasProductosID", empresaId.Value,
                    body.MarcasProductos.Select(m => (m.MarcasProductosID, m.Nombre, extraCols: "", extraVals: "", Array.Empty<object>())));

                resultado["familiasProductos"] = await UpsertPorEmpresaAsync(conn, "dbo.FamiliasProductos", "FamiliaProductosID", empresaId.Value,
                    body.FamiliasProductos.Select(f => (f.FamiliaProductosID, f.Nombre, extraCols: "", extraVals: "", Array.Empty<object>())));

                resultado["categorias"] = await UpsertPorEmpresaAsync(conn, "dbo.CategoriasProductos", "CategoriasProductosID", empresaId.Value,
                    body.Categorias.Select(c => (c.CategoriasProductosID, c.Nombre, extraCols: "", extraVals: "", Array.Empty<object>())));

                resultado["subCategorias"] = await UpsertPorEmpresaAsync(conn, "dbo.SubCategoriasProductos", "SubCategoriasProductosID", empresaId.Value,
                    body.SubCategorias.Select(s => (s.SubCategoriasProductosID, s.Nombre, extraCols: "", extraVals: "", Array.Empty<object>())));

                resultado["categoriasComerciales"] = await UpsertPorEmpresaAsync(conn, "dbo.CategoriasComerciales", "CategoriasComercialesID", empresaId.Value,
                    body.CategoriasComerciales.Select(c => (c.CategoriasComercialesID, c.Nombre, extraCols: "", extraVals: "", Array.Empty<object>())));

                resultado["listasPrecios"] = await UpsertPorEmpresaAsync(conn, "dbo.ListasPrecios", "ListasPreciosID", empresaId.Value,
                    body.ListasPrecios.Select(l => (l.ListasPreciosID, l.Nombre, extraCols: ", Baja, MontoMinimo, PorcentajeMarcup", extraVals: ", @Extra1, @Extra2, @Extra3", (object[])[l.Baja, l.MontoMinimo, l.PorcentajeMarcup])));

                resultado["tiposDocumentos"] = await UpsertTiposDocumentosAsync(conn, body.TiposDocumentos);

                return Results.Ok(resultado);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error sync maestros: {ex.Message}");
            }
        })
        .WithName("SyncMaestros")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Upsert genérico para los catálogos que dependen de EmpresaID (PK compuesta
    /// EmpresaID+IdColumn). Valida que Id&gt;0 y Nombre no vacío antes de aplicar el MERGE.
    /// </summary>
    private static async Task<object> UpsertPorEmpresaAsync(
        SqlConnection conn, string table, string idColumn, int empresaId,
        IEnumerable<(int Id, string Nombre, string extraCols, string extraVals, object[] extraParams)> items)
    {
        int procesados = 0, invalidos = 0;
        foreach (var (id, nombreRaw, extraCols, extraVals, extraParams) in items)
        {
            string nombre = (nombreRaw ?? "").Trim();
            if (id <= 0 || nombre.Length == 0)
            {
                invalidos++;
                continue;
            }

            string sql = $@"
                MERGE {table} AS tgt
                USING (SELECT @EmpresaID AS EmpresaID, @Id AS Id) AS src
                    ON tgt.EmpresaID = src.EmpresaID AND tgt.{idColumn} = src.Id
                WHEN MATCHED THEN UPDATE SET Nombre = @Nombre{ExpandSetClause(extraCols)}
                WHEN NOT MATCHED THEN INSERT (EmpresaID, {idColumn}, Nombre{extraCols})
                    VALUES (@EmpresaID, @Id, @Nombre{extraVals});";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@EmpresaID", empresaId);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@Nombre", nombre);
            for (int i = 0; i < extraParams.Length; i++)
                cmd.Parameters.AddWithValue($"@Extra{i + 1}", extraParams[i]);

            await cmd.ExecuteNonQueryAsync();
            procesados++;
        }

        return new { procesados, invalidos };
    }

    // Convierte ", Porcentaje" -> ", Porcentaje = @Extra1" para el UPDATE SET del MERGE.
    private static string ExpandSetClause(string extraCols)
    {
        if (string.IsNullOrEmpty(extraCols))
            return "";

        var nombres = extraCols.TrimStart(',', ' ').Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(nombres.Select((n, i) => $", {n} = @Extra{i + 1}"));
    }

    // TiposDocumentos es un catálogo global (no depende de EmpresaID) — igual que
    // en el sistema real, donde "DNI"/"CUIT" no varían de una empresa a otra.
    private static async Task<object> UpsertTiposDocumentosAsync(SqlConnection conn, List<TipoDocumentoSyncItem> items)
    {
        int procesados = 0, invalidos = 0;
        foreach (var t in items)
        {
            string nombre = (t.Nombre ?? "").Trim();
            if (t.TiposDocumentosID <= 0 || nombre.Length == 0)
            {
                invalidos++;
                continue;
            }

            const string sql = @"
                MERGE dbo.TiposDocumentos AS tgt
                USING (SELECT @Id AS Id) AS src ON tgt.TiposDocumentosID = src.Id
                WHEN MATCHED THEN UPDATE SET Nombre = @Nombre
                WHEN NOT MATCHED THEN INSERT (TiposDocumentosID, Nombre) VALUES (@Id, @Nombre);";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", t.TiposDocumentosID);
            cmd.Parameters.AddWithValue("@Nombre", nombre);
            await cmd.ExecuteNonQueryAsync();
            procesados++;
        }

        return new { procesados, invalidos };
    }
}

// DTOs de request — mismos nombres de propiedad que envía SincroApp-Demo
// (System.Text.Json en ASP.NET Core matchea sin distinguir mayúsculas/minúsculas).

public sealed class ClienteSyncItem
{
    public string? Codigo { get; set; }
    public string? Nombre { get; set; }
    public string? Direccion { get; set; }
    public string? Localidad { get; set; }
    public string? Telefono { get; set; }
    public int DiaDeVenta { get; set; }
    public decimal PorcentajePercepcionIB { get; set; }
    public double Latitud { get; set; }
    public double Longitud { get; set; }
    public int ListasPreciosID { get; set; }
    public int TiposDocumentosID { get; set; }
    public string? NumeroDocumento { get; set; }
    public bool Baja { get; set; }
}

public sealed class ProductoSyncItem
{
    public string? CodigoDeProducto { get; set; }
    public string? Nombre { get; set; }
    public int? ProductosIDPadre { get; set; }
    public int StockEnUnidades { get; set; }
    public int CantidadMultiplo { get; set; }
    public bool DesactivadoParaLaVenta { get; set; }
    public int MarcasProductosID { get; set; }
    public int FamiliaProductosID { get; set; }
    public bool StockPropio { get; set; }
    public List<int> CategoriasComercialesIDs { get; set; } = new();
    public int UnidadesPorBulto { get; set; }
    public decimal PrecioProducto { get; set; }
    public int ImpuestosID { get; set; }
    public bool Baja { get; set; }
    public int CategoriasProductosID { get; set; }
    public int SubCategoriasProductosID { get; set; }
}

public sealed class StockSyncItem
{
    public string? CodigoProducto { get; set; }
    public string StockUnidades { get; set; } = "";
}

public sealed class MaestrosSyncRequest
{
    public List<ImpuestoSyncItem> Impuestos { get; set; } = new();
    public List<MarcaProductoSyncItem> MarcasProductos { get; set; } = new();
    public List<FamiliaProductoSyncItem> FamiliasProductos { get; set; } = new();
    public List<CategoriaProductoSyncItem> Categorias { get; set; } = new();
    public List<SubCategoriaProductoSyncItem> SubCategorias { get; set; } = new();
    public List<CategoriaComercialSyncItem> CategoriasComerciales { get; set; } = new();
    public List<ListaPrecioSyncItem> ListasPrecios { get; set; } = new();
    public List<TipoDocumentoSyncItem> TiposDocumentos { get; set; } = new();
}

public sealed class ImpuestoSyncItem { public int ImpuestosID { get; set; } public string? Nombre { get; set; } public decimal Porcentaje { get; set; } }
public sealed class MarcaProductoSyncItem { public int MarcasProductosID { get; set; } public string? Nombre { get; set; } }
public sealed class FamiliaProductoSyncItem { public int FamiliaProductosID { get; set; } public string? Nombre { get; set; } }
public sealed class CategoriaProductoSyncItem { public int CategoriasProductosID { get; set; } public string? Nombre { get; set; } }
public sealed class SubCategoriaProductoSyncItem { public int SubCategoriasProductosID { get; set; } public string? Nombre { get; set; } }
public sealed class CategoriaComercialSyncItem { public int CategoriasComercialesID { get; set; } public string? Nombre { get; set; } }
public sealed class ListaPrecioSyncItem { public int ListasPreciosID { get; set; } public string? Nombre { get; set; } public bool Baja { get; set; } public decimal MontoMinimo { get; set; } public decimal PorcentajeMarcup { get; set; } }
public sealed class TipoDocumentoSyncItem { public int TiposDocumentosID { get; set; } public string? Nombre { get; set; } }
