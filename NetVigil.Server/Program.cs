var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazorClient",
        policy =>
        {
            policy.WithOrigins("https://localhost:7031", "http://localhost:5001")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});
builder.Services.AddSingleton<NetVigil.Server.Services.SimulationService>();
builder.Services.AddGrpc();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();

app.UseCors("AllowBlazorClient");

app.MapGrpcService<NetVigil.Server.Services.GrpcScanService>();

app.UseAuthorization();

app.MapControllers();

app.Run();
