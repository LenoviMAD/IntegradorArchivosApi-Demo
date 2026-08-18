using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace IntegradorArchivosApi.Demo.Database;

/// <summary>
/// Crea (si hace falta) la base IntegradorDemo, sus tablas, el SP de ejemplo
/// y siembra datos de demo. Pensado para que "dotnet run" deje todo listo
/// contra SQL Server LocalDB sin pasos manuales.
/// </summary>
public static class SchemaInitializer
{
    public static async Task EnsureDatabaseAsync(string connectionString, ILogger logger)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        string databaseName = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        // 1. Crear la base si no existe (hay que conectar a master, no se puede
        //    hacer CREATE DATABASE dentro de una conexión ya abierta contra ella).
        await using (var masterConn = new SqlConnection(builder.ConnectionString))
        {
            await masterConn.OpenAsync();

            const string sqlExiste = "SELECT database_id FROM sys.databases WHERE name = @Nombre";
            await using var cmdExiste = new SqlCommand(sqlExiste, masterConn);
            cmdExiste.Parameters.AddWithValue("@Nombre", databaseName);
            object? existe = await cmdExiste.ExecuteScalarAsync();

            if (existe == null || existe == DBNull.Value)
            {
                logger.LogInformation("Base {DatabaseName} no existe. Creándola...", databaseName);
                // El nombre de la base no puede parametrizarse: viene de nuestro propio
                // appsettings.json (no de input de usuario), así que se arma inline.
                await using var cmdCreate = new SqlCommand($"CREATE DATABASE [{databaseName}]", masterConn);
                await cmdCreate.ExecuteNonQueryAsync();
            }
        }

        // 2. Conectar ya contra IntegradorDemo y crear tablas/SP si hace falta.
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        const string sqlTablaExiste = @"
            SELECT OBJECT_ID('dbo.Usuarios')
        ";
        bool coreSchemaExiste;
        await using (var cmdChk = new SqlCommand(sqlTablaExiste, conn))
        {
            object? tablaExiste = await cmdChk.ExecuteScalarAsync();
            coreSchemaExiste = tablaExiste != null && tablaExiste != DBNull.Value;
        }

        if (coreSchemaExiste)
        {
            logger.LogInformation("Esquema de {DatabaseName} ya existe. No se vuelve a crear.", databaseName);
        }
        else
        {
            await CrearEsquemaBaseYSembrarAsync(conn, databaseName, logger);
        }

        // Se ejecuta siempre (idempotente): agrega el esquema de sincronización
        // (clientes, maestros, columnas extra de productos) aunque la base ya
        // existiera de una corrida anterior a que existiera este feature.
        await EnsureSyncSchemaAsync(conn, logger);
    }

    private static async Task CrearEsquemaBaseYSembrarAsync(SqlConnection conn, string databaseName, ILogger logger)
    {
        logger.LogInformation("Creando esquema y sembrando datos de demo en {DatabaseName}...", databaseName);

        const string sqlSchema = @"
            CREATE TABLE dbo.Usuarios
            (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                Usuario    NVARCHAR(50)  NOT NULL,
                ClaveHash  CHAR(64)      NOT NULL,
                EmpresaID  INT           NOT NULL,
                Baja       BIT           NOT NULL DEFAULT 0
            );

            CREATE TABLE dbo.Productos
            (
                Id        INT IDENTITY(1,1) PRIMARY KEY,
                Codigo    NVARCHAR(30)   NOT NULL,
                Nombre    NVARCHAR(200)  NOT NULL,
                Precio    DECIMAL(18,2)  NOT NULL,
                Stock     INT            NOT NULL DEFAULT 0,
                EmpresaID INT            NOT NULL
            );

            CREATE TABLE dbo.Pedidos
            (
                Id        INT IDENTITY(1,1) PRIMARY KEY,
                EmpresaID INT            NOT NULL,
                Fecha     DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
                Estado    NVARCHAR(20)   NOT NULL DEFAULT 'Pendiente'
            );

            CREATE TABLE dbo.PedidosDetalle
            (
                Id          INT IDENTITY(1,1) PRIMARY KEY,
                PedidoId    INT     NOT NULL REFERENCES dbo.Pedidos(Id),
                ProductoId  INT     NOT NULL REFERENCES dbo.Productos(Id),
                Cantidad    INT     NOT NULL
            );
        ";

        await using (var cmdSchema = new SqlCommand(sqlSchema, conn))
            await cmdSchema.ExecuteNonQueryAsync();

        // sp_RegistrarPedido: inserta un Pedido + su detalle en una única transacción.
        // Recibe el detalle como JSON (OPENJSON) para no depender de un TVP creado aparte,
        // manteniendo el script de creación autocontenido en un solo lugar.
        const string sqlProc = @"
            CREATE PROCEDURE dbo.sp_RegistrarPedido
                @EmpresaID   INT,
                @DetalleJson NVARCHAR(MAX),
                @PedidosID   INT OUTPUT
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;

                BEGIN TRANSACTION;

                INSERT INTO dbo.Pedidos (EmpresaID, Fecha, Estado)
                VALUES (@EmpresaID, SYSUTCDATETIME(), 'Pendiente');

                SET @PedidosID = SCOPE_IDENTITY();

                INSERT INTO dbo.PedidosDetalle (PedidoId, ProductoId, Cantidad)
                SELECT @PedidosID, ProductoId, Cantidad
                FROM OPENJSON(@DetalleJson)
                WITH (
                    ProductoId INT '$.ProductoId',
                    Cantidad   INT '$.Cantidad'
                );

                COMMIT TRANSACTION;
            END
        ";

        await using (var cmdProc = new SqlCommand(sqlProc, conn))
            await cmdProc.ExecuteNonQueryAsync();

        await SeedAsync(conn);

        logger.LogInformation("Esquema y datos de demo listos.");
    }

    private static async Task SeedAsync(SqlConnection conn)
    {
        // Usuarios demo — mismo mecanismo de hash que el sistema real: SHA256 en hex.
        (string usuario, string clave, int empresaId)[] usuarios =
        {
            ("demo",  "Demo123!",  1),
            ("admin", "Admin123!", 1),
        };

        const string sqlUsuario = @"
            INSERT INTO dbo.Usuarios (Usuario, ClaveHash, EmpresaID, Baja)
            VALUES (@Usuario, @ClaveHash, @EmpresaID, 0)";

        foreach (var (usuario, clave, empresaId) in usuarios)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clave)));
            await using var cmd = new SqlCommand(sqlUsuario, conn);
            cmd.Parameters.AddWithValue("@Usuario", usuario);
            cmd.Parameters.AddWithValue("@ClaveHash", hash);
            cmd.Parameters.AddWithValue("@EmpresaID", empresaId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Productos demo (inventados, sin relación con datos reales de cliente).
        (string codigo, string nombre, decimal precio, int stock)[] productos =
        {
            ("P001", "Cuaderno tapa dura A4",        3200.00m, 120),
            ("P002", "Lapicera tinta gel azul",        850.00m, 300),
            ("P003", "Resma papel A4 x500",           7100.00m,  60),
            ("P004", "Carpeta plástica N3",           1450.00m, 150),
            ("P005", "Marcador permanente negro",      690.00m, 200),
            ("P006", "Corrector líquido 20ml",         540.00m, 180),
            ("P007", "Tijera de oficina 20cm",        1980.00m,  70),
            ("P008", "Cinta adhesiva ancha",           610.00m, 250),
            ("P009", "Broches mariposa x50",           430.00m,  90),
            ("P010", "Agenda anual tapa flexible",    5200.00m,  40),
        };

        const string sqlProducto = @"
            INSERT INTO dbo.Productos (Codigo, Nombre, Precio, Stock, EmpresaID)
            VALUES (@Codigo, @Nombre, @Precio, @Stock, @EmpresaID)";

        foreach (var (codigo, nombre, precio, stock) in productos)
        {
            await using var cmd = new SqlCommand(sqlProducto, conn);
            cmd.Parameters.AddWithValue("@Codigo", codigo);
            cmd.Parameters.AddWithValue("@Nombre", nombre);
            cmd.Parameters.AddWithValue("@Precio", precio);
            cmd.Parameters.AddWithValue("@Stock", stock);
            cmd.Parameters.AddWithValue("@EmpresaID", 1);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Esquema para los 4 endpoints de sincronización masiva (clientes, productos,
    /// stock, maestros) que usa SincroApp-Demo. Se ejecuta en cada arranque y es
    /// idempotente (IF OBJECT_ID/COL_LENGTH) para no romper una base creada antes
    /// de que este feature existiera.
    /// </summary>
    private static async Task EnsureSyncSchemaAsync(SqlConnection conn, ILogger logger)
    {
        const string sql = @"
            IF OBJECT_ID('dbo.Clientes') IS NULL
            BEGIN
                CREATE TABLE dbo.Clientes
                (
                    ClientesID             INT IDENTITY(1,1) PRIMARY KEY,
                    EmpresaID              INT            NOT NULL,
                    Codigo                 NVARCHAR(30)   NOT NULL,
                    Nombre                 NVARCHAR(200)  NOT NULL,
                    Direccion              NVARCHAR(200)  NULL,
                    Localidad              NVARCHAR(100)  NULL,
                    Telefono               NVARCHAR(50)   NULL,
                    DiaDeVenta             INT            NOT NULL DEFAULT 0,
                    PorcentajePercepcionIB DECIMAL(5,2)   NOT NULL DEFAULT 0,
                    Latitud                FLOAT          NULL,
                    Longitud               FLOAT          NULL,
                    ListasPreciosID        INT            NULL,
                    TiposDocumentosID      INT            NULL,
                    NumeroDocumento        NVARCHAR(30)   NULL,
                    Baja                   BIT            NOT NULL DEFAULT 0,
                    FechaActualizacion     DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT UQ_Clientes_EmpresaCodigo UNIQUE (EmpresaID, Codigo)
                );
            END

            IF OBJECT_ID('dbo.Impuestos') IS NULL
            BEGIN
                CREATE TABLE dbo.Impuestos
                (
                    EmpresaID  INT           NOT NULL,
                    ImpuestosID INT          NOT NULL,
                    Nombre     NVARCHAR(100) NOT NULL,
                    Porcentaje DECIMAL(5,2)  NOT NULL,
                    PRIMARY KEY (EmpresaID, ImpuestosID)
                );
            END

            IF OBJECT_ID('dbo.MarcasProductos') IS NULL
            BEGIN
                CREATE TABLE dbo.MarcasProductos
                (
                    EmpresaID          INT           NOT NULL,
                    MarcasProductosID  INT           NOT NULL,
                    Nombre             NVARCHAR(100) NOT NULL,
                    PRIMARY KEY (EmpresaID, MarcasProductosID)
                );
            END

            IF OBJECT_ID('dbo.FamiliasProductos') IS NULL
            BEGIN
                CREATE TABLE dbo.FamiliasProductos
                (
                    EmpresaID           INT           NOT NULL,
                    FamiliaProductosID  INT           NOT NULL,
                    Nombre              NVARCHAR(100) NOT NULL,
                    PRIMARY KEY (EmpresaID, FamiliaProductosID)
                );
            END

            IF OBJECT_ID('dbo.CategoriasProductos') IS NULL
            BEGIN
                CREATE TABLE dbo.CategoriasProductos
                (
                    EmpresaID              INT           NOT NULL,
                    CategoriasProductosID  INT           NOT NULL,
                    Nombre                 NVARCHAR(100) NOT NULL,
                    PRIMARY KEY (EmpresaID, CategoriasProductosID)
                );
            END

            IF OBJECT_ID('dbo.SubCategoriasProductos') IS NULL
            BEGIN
                CREATE TABLE dbo.SubCategoriasProductos
                (
                    EmpresaID                INT           NOT NULL,
                    SubCategoriasProductosID INT           NOT NULL,
                    Nombre                   NVARCHAR(100) NOT NULL,
                    PRIMARY KEY (EmpresaID, SubCategoriasProductosID)
                );
            END

            IF OBJECT_ID('dbo.CategoriasComerciales') IS NULL
            BEGIN
                CREATE TABLE dbo.CategoriasComerciales
                (
                    EmpresaID                INT           NOT NULL,
                    CategoriasComercialesID  INT           NOT NULL,
                    Nombre                   NVARCHAR(100) NOT NULL,
                    PRIMARY KEY (EmpresaID, CategoriasComercialesID)
                );
            END

            IF OBJECT_ID('dbo.ListasPrecios') IS NULL
            BEGIN
                CREATE TABLE dbo.ListasPrecios
                (
                    EmpresaID        INT           NOT NULL,
                    ListasPreciosID  INT           NOT NULL,
                    Nombre           NVARCHAR(100) NOT NULL,
                    Baja             BIT           NOT NULL DEFAULT 0,
                    MontoMinimo      DECIMAL(18,2) NOT NULL DEFAULT 0,
                    PorcentajeMarcup DECIMAL(5,2)  NOT NULL DEFAULT 0,
                    PRIMARY KEY (EmpresaID, ListasPreciosID)
                );
            END

            IF OBJECT_ID('dbo.TiposDocumentos') IS NULL
            BEGIN
                -- Catálogo global (no varía por empresa), igual que en el sistema real.
                CREATE TABLE dbo.TiposDocumentos
                (
                    TiposDocumentosID INT           NOT NULL PRIMARY KEY,
                    Nombre            NVARCHAR(100) NOT NULL
                );
            END

            IF COL_LENGTH('dbo.Productos','MarcasProductosID') IS NULL
                ALTER TABLE dbo.Productos ADD MarcasProductosID INT NULL;
            IF COL_LENGTH('dbo.Productos','FamiliaProductosID') IS NULL
                ALTER TABLE dbo.Productos ADD FamiliaProductosID INT NULL;
            IF COL_LENGTH('dbo.Productos','ImpuestosID') IS NULL
                ALTER TABLE dbo.Productos ADD ImpuestosID INT NULL;
            IF COL_LENGTH('dbo.Productos','SubCategoriasProductosID') IS NULL
                ALTER TABLE dbo.Productos ADD SubCategoriasProductosID INT NULL;
            IF COL_LENGTH('dbo.Productos','CategoriasComercialesID') IS NULL
                ALTER TABLE dbo.Productos ADD CategoriasComercialesID INT NULL;
            IF COL_LENGTH('dbo.Productos','ProductosIDPadre') IS NULL
                ALTER TABLE dbo.Productos ADD ProductosIDPadre INT NULL;
            IF COL_LENGTH('dbo.Productos','DesactivadoParaLaVenta') IS NULL
                ALTER TABLE dbo.Productos ADD DesactivadoParaLaVenta BIT NOT NULL DEFAULT 0;
            IF COL_LENGTH('dbo.Productos','CantidadMultiplo') IS NULL
                ALTER TABLE dbo.Productos ADD CantidadMultiplo INT NOT NULL DEFAULT 1;
            IF COL_LENGTH('dbo.Productos','UnidadesPorBulto') IS NULL
                ALTER TABLE dbo.Productos ADD UnidadesPorBulto INT NOT NULL DEFAULT 1;
            IF COL_LENGTH('dbo.Productos','StockPropio') IS NULL
                ALTER TABLE dbo.Productos ADD StockPropio BIT NOT NULL DEFAULT 1;
            IF COL_LENGTH('dbo.Productos','Baja') IS NULL
                ALTER TABLE dbo.Productos ADD Baja BIT NOT NULL DEFAULT 0;
        ";

        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        logger.LogInformation("Esquema de sincronización (Clientes/Productos/Maestros) listo.");
    }
}
