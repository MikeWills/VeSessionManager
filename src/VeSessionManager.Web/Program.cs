using Microsoft.EntityFrameworkCore;
using Serilog;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Square;
using VeSessionManager.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton(TimeProvider.System);
// No SquareOptions here — WebhookSignatureKey/WebhookNotificationUrl now live on Team (multi-team,
// each team verifies against its own key via the /webhooks/square/{teamId} route). Nothing else in
// this project needs SquareOptions:Environment.
builder.Services.AddScoped<SquareWebhookHandler>();

// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapSquareWebhook();

app.Run();
