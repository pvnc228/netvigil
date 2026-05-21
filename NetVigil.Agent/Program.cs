using NetVigil.Agent;

var builder = Host.CreateApplicationBuilder(args);

var mode = builder.Configuration["Agent:Mode"]?.Trim();

if (string.Equals(mode, "Synthetic", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService<SyntheticGenerator>();
}
else
{
    builder.Services.AddHostedService<Worker>();

    if (string.Equals(mode, "GatewaySniffer", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddHostedService<GatewaySniffer>();
    }
}

var host = builder.Build();
host.Run();
