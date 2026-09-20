using DotCode.Web.Components;
using DotCode.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents(o => o.DetailedErrors = true)
    .AddInteractiveServerComponents(o => o.DetailedErrors = true)
    .AddInteractiveWebAssemblyComponents();

var serverUrl = builder.Configuration["ServerUrl"] ?? "http://127.0.0.1:4096/";
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(serverUrl) });
builder.Services.AddScoped<DotCodeApi>();
builder.Services.AddScoped<SessionUiState>();
builder.Services.AddScoped<ToastService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(DotCode.Web.Client._Imports).Assembly);

app.Run();
