using Lending.Api;
using Lending.Application;
using Lending.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new SqlConnectionFactory(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Lending") ?? ""));
builder.Services.AddScoped<IAgreementSessionFactory, SqlAgreementSessionFactory>();
builder.Services.AddScoped<ILendingQueries, SqlLendingQueries>();
builder.Services.AddScoped<CreateAgreementCommandHandler>();
builder.Services.AddScoped<ActivateAgreementCommandHandler>();

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Lending API v1");
    });
}
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).ExcludeFromDescription();
app.MapGet("/health/ready", async (SqlConnectionFactory connections, CancellationToken cancellationToken) =>
{
    await using var connection = await connections.OpenAsync(cancellationToken);
    return Results.Ok(new { status = "ready" });
}).ExcludeFromDescription();
app.MapControllers();
app.Run();

namespace Lending.Api
{
    public partial class Program;
}
