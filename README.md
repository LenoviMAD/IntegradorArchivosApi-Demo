# IntegradorArchivosApi

API para integrar archivos de sincronización (productos, clientes, stock, maestros) entre una app de escritorio y un backend central. La armé con **ASP.NET Core Minimal API** sobre **.NET 9**, usando **ADO.NET puro** en vez de un ORM, para practicar y mostrar ese estilo de acceso a datos: SQL inline con `SqlConnection`/`SqlCommand`, sin repositorios genéricos de por medio.

## Qué implementa

- Login con token simple: `POST /api/auth/login` valida usuario/clave (hash SHA-256 contra la tabla `Usuarios`) y devuelve un token random de 32 bytes (hex), válido 24hs, guardado en memoria (`ConcurrentDictionary<string, (int EmpresaID, DateTime ExpiresAt)>`).
- `ValidateBearer(HttpRequest)` parsea el header `Authorization: Bearer …` y valida contra ese diccionario en cada endpoint protegido.
- Catálogo de productos y alta de producto.
- Registro de pedidos vía un stored procedure (`sp_RegistrarPedido`) que inserta cabecera + detalle dentro de una transacción.
- Cuatro endpoints de sincronización masiva (clientes, productos, stock, maestros) que consume [`SincroApp-Demo`](https://github.com/LenoviMAD/SincroApp-Demo), con validación real de los datos recibidos antes de aplicarlos (campos requeridos, tipos, referencias) — no responden 200 OK a ciegas.

## Requisitos

- .NET SDK 9
- SQL Server LocalDB (`(localdb)\MSSQLLocalDB`)

No hace falta crear la base a mano: al arrancar, la aplicación la crea (si no existe), crea las tablas, el stored procedure, y siembra datos de prueba.

## Cómo correrlo

```bash
dotnet run
```

En el primer arranque vas a ver en el log que crea la base `IntegradorDemo`, las tablas y los datos semilla. En arranques siguientes detecta que el esquema ya existe y no vuelve a crear nada.

La API queda escuchando en `http://localhost:5080` (ver `Properties/launchSettings.json`).

Usá `IntegradorArchivosApi.Demo.http` (VS Code REST Client / Rider / Visual Studio) para probar el flujo completo: login → catálogo → alta de producto → registrar pedido → ver pedidos pendientes.

## API a la que se conecta

Ninguna — es standalone, habla directo con su propia base SQL Server LocalDB vía ADO.NET. Es esta misma API la que consume [`SincroApp-Demo`](https://github.com/LenoviMAD/SincroApp-Demo) como backend.

## Credenciales

| Usuario | Clave       | EmpresaID |
|---------|-------------|-----------|
| demo    | Demo123!    | 1         |
| admin   | Admin123!   | 1         |

## Esquema

- **Usuarios** (Id, Usuario, ClaveHash, EmpresaID, Baja)
- **Productos** (Id, Codigo, Nombre, Precio, Stock, EmpresaID, MarcasProductosID, FamiliaProductosID,
  ImpuestosID, SubCategoriasProductosID, CategoriasComercialesID, ProductosIDPadre,
  DesactivadoParaLaVenta, CantidadMultiplo, UnidadesPorBulto, StockPropio, Baja)
- **Pedidos** (Id, EmpresaID, Fecha, Estado)
- **PedidosDetalle** (Id, PedidoId, ProductoId, Cantidad)
- **Clientes** (ClientesID, EmpresaID, Codigo, Nombre, Direccion, Localidad, Telefono, DiaDeVenta,
  PorcentajePercepcionIB, Latitud, Longitud, ListasPreciosID, TiposDocumentosID, NumeroDocumento, Baja)
- **Impuestos / MarcasProductos / FamiliasProductos / CategoriasProductos / SubCategoriasProductos /
  CategoriasComerciales / ListasPrecios** — catálogos por empresa (EmpresaID + ID + Nombre)
- **TiposDocumentos** — catálogo global (sin EmpresaID)
- **sp_RegistrarPedido** — inserta un Pedido + su detalle en una transacción

## Endpoints

- `POST /api/auth/login` — login, devuelve `{ empresaID, token }`
- `GET  /api/productos` — catálogo de la empresa del token (protegido)
- `POST /api/productos` — alta de **un** producto (protegido)
- `POST /api/pedidos` — registra un pedido vía `sp_RegistrarPedido` (protegido)
- `GET  /api/pedidos/pendientes/{empresaId}` — pedidos pendientes con su detalle (protegido)
- `POST /api/sync/clientes` — upsert masivo de clientes (protegido)
- `POST /api/sync/productos` — upsert masivo de productos (protegido, distinto de `POST /api/productos`)
- `POST /api/sync/stock` — actualiza solo el stock de productos existentes (protegido)
- `POST /api/sync/maestros` — upsert de los 8 catálogos de referencia (protegido)
