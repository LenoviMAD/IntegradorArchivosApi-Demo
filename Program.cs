using IntegradorArchivosApi.Demo.Database;
using IntegradorArchivosApi.Demo.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

var connStr = app.Configuration.GetConnectionString("IntegradorDemo")!;

// Crea la base IntegradorDemo, tablas, SP y seed si no existen todavía.
await SchemaInitializer.EnsureDatabaseAsync(connStr, app.Logger);

app.MapAuthEndpoints(connStr);
app.MapProductosEndpoints(connStr);
app.MapPedidosEndpoints(connStr);
app.MapSyncEndpoints(connStr);

app.Run();
