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

builder.Services.AddControllersWithViews();
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

// In-memory store of run results (bounded; demo-suitable).
builder.Services.AddSingleton<RunStore>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
