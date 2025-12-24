using Microsoft.AspNetCore.Server.Kestrel.Core;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.WebHost.ConfigureKestrel(options =>
{
    // Порт 8080: Для Браузера (REST API, файлы сайта) - только HTTP/1
    options.ListenAnyIP(8080, o => o.Protocols = HttpProtocols.Http1);

    // Порт 8081: Для Агента (gRPC) - только HTTP/2
    options.ListenAnyIP(8081, o => o.Protocols = HttpProtocols.Http2);
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy =>
        {
            policy.WithOrigins("https://localhost:7031", "http://localhost:5001")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});
builder.Services.AddSingleton<NetVigil.Server.Services.SimulationService>();
builder.Services.AddSingleton<NetVigil.Server.Services.NotificationService>();
builder.Services.AddGrpc();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.MapGrpcService<NetVigil.Server.Services.GrpcScanService>();

app.UseAuthorization();

app.MapControllers();

app.Run();
