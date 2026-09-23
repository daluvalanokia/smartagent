using System.Collections.Concurrent;
using SmartAgent.Core;
using SmartAgent.Domain;
using SmartAgent.Integrations;
using SmartAgent.Integrations.AI;
using SmartAgent.Integrations.Reasoning;
using SmartAgent.SourceProviders;
using SmartAgent.Web.Models;
using SmartAgent.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddControllers();   // CI/CD API surface (api/validate, runs, feedback, targets)
builder.Services.Configure<RunOptions>(o => { });  // sensible defaults from domain

builder.Services.AddSingleton<AiProviderOptions>(builder.Configuration.GetSection(AiProviderOptions.SectionName).Get<AiProviderOptions>() ?? new());
builder.Services.AddSingleton<ReasoningOptions>(builder.Configuration.GetSection(ReasoningOptions.SectionName).Get<ReasoningOptions>() ?? new());

builder.Services.AddHttpClient("smartagent").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Snapshots may include many parallel calls; be generous but bounded.
    MaxConnectionsPerServer = 8
});

builder.Services.AddScoped<ZipSourceProvider>();
builder.Services.AddHttpClient<GitHubSourceProvider>().SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<WebsiteSourceProvider>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    });

// expose all three providers to the engine as ISourceProvider
builder.Services.AddScoped<ISourceProvider>(sp => sp.GetRequiredService<ZipSourceProvider>());
builder.Services.AddScoped<ISourceProvider>(sp => sp.GetRequiredService<GitHubSourceProvider>());
builder.Services.AddScoped<ISourceProvider>(sp => sp.GetRequiredService<WebsiteSourceProvider>());

builder.Services.AddHttpClient("ai");
builder.Services.AddScoped<OpenAiChatClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new OpenAiChatClient(factory.CreateClient("ai"), sp.GetRequiredService<AiProviderOptions>());
});
builder.Services.AddScoped<IChatCompletionClient>(sp => sp.GetRequiredService<OpenAiChatClient>());

builder.Services.AddHttpClient("reasoning");
builder.Services.AddScoped<HttpReasoningService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new HttpReasoningService(factory.CreateClient("reasoning"), sp.GetRequiredService<ReasoningOptions>());
});
builder.Services.AddScoped<IReasoningService>(sp => sp.GetRequiredService<HttpReasoningService>());

builder.Services.AddSingleton<HeuristicAnalyzer>();
builder.Services.AddSingleton<Prioritizer>();
builder.Services.AddScoped<PromptBuilder>();
builder.Services.AddScoped<SmartAgentEngine>();

// Parallel prompt-evaluation pipeline: plan → bounded multi-thread execution → consolidation.
builder.Services.AddSingleton<WorkPlanner>();
builder.Services.AddSingleton<IWorkUnitEvaluator, HeuristicWorkUnitEvaluator>();
builder.Services.AddSingleton<ParallelWorkExecutor>();
builder.Services.AddSingleton<PromptConsolidator>();
builder.Services.AddSingleton<PromptEvaluator>();

// SAAEL — SmartAgent Autonomous Agile Engineering Lifecycle: role agents + orchestrator.
builder.Services.AddSingleton<BaAgent>();
builder.Services.AddSingleton<PmAgent>();
builder.Services.AddSingleton<DeveloperAgent>();
builder.Services.AddSingleton<QaAgent>();
builder.Services.AddSingleton<CiAgent>();
builder.Services.AddSingleton<LearningAgent>();
builder.Services.AddSingleton<SaaelOrchestrator>();
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
builder.Services.AddSingleton<AppAnalyzerAgent>();

// In-memory store of run results (bounded; demo-suitable).
builder.Services.AddSingleton<RunStore>();

// CI/CD continuity registry: persisted cross-run finding history per target.
builder.Services.AddSingleton<ContinuityRegistry>(sp =>
{
    var path = sp.GetRequiredService<IConfiguration>()["SmartAgent:Continuity:StorePath"] ?? "data/continuity.json";
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new ContinuityRegistry(Path.Combine(env.ContentRootPath, path));
});

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
