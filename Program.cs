using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// UI
builder.Services.AddRazorPages();

// API
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// Optional but useful
builder.Services.AddMemoryCache();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Prevent browsers from caching dynamic pages (LAW query results must always be fresh)
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.Run();
