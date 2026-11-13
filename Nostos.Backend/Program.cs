using Nostos.Backend.Composition;
using Nostos.Product.Composition;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureSelfHostedHttpHost();

// Pre-product SelfHosted overrides: register concrete acquisition staging,
// local book-text persistence, and BYOK embeddings before AddNostosProduct so
// its TryAdd no-op fallbacks are never selected.
builder.Services.AddSelfHostedCoreOverrides();
builder.Services.AddSelfHostedEmbeddingProvider();

var product = builder.Services.AddNostosProduct(builder.Configuration);

// Post-product SelfHosted composition: integrations, resolved paths & storage,
// AI transports/settings, migration/activation/recovery portability, and workers.
builder.Services.AddSelfHostedIntegrations();
var storagePaths = builder.Services.AddSelfHostedPathsAndStorage(
    builder.Configuration,
    builder.Environment.ContentRootPath);
builder.Services.AddSelfHostedAiServices(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    product.Assistant);
builder.Services.AddSelfHostedPortability(builder.Configuration, storagePaths);
builder.Services.AddSelfHostedWorkers();

var app = builder.Build();

await app.InitializeSelfHostedHostAsync();
app.UseSelfHostedHttpPipeline(product.Opds);
SelfHostedActivationDrillHook.ConfigureIfEnabled(app);

app.Run();
return 0;

public partial class Program;
