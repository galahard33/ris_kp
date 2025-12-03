using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Для Docker всегда используем Development окружение
builder.Environment.EnvironmentName = "Development";

// Отключаем DataProtection в Docker - используем эпиhemeral провайдер
builder.Services.AddDataProtection()
    .UseEphemeralDataProtectionProvider();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500_000_000;
    options.ValueLengthLimit = 500_000_000;
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 500_000_000;
});

builder.Services.AddControllersWithViews();

var app = builder.Build();

// Всегда показываем страницу ошибок разработчика в Docker
app.UseDeveloperExceptionPage();

// Отключаем HTTPS редирект в Docker
// app.UseHttpsRedirection(); // Закомментируйте или удалите

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();