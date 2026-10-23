using System.Text;
using JugglucoRelay;
using JugglucoRelay.Export;
using JugglucoRelay.Relay;
using JugglucoRelay.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddSystemd();
builder.Services.AddOptions<RelayOptions>()
    .Bind(builder.Configuration.GetSection(RelayOptions.Section))
    .Validate(o => Validate(o) is null, "Invalid configuration")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => new FileStore(Path.Combine(DataDir(sp), "files")));
builder.Services.AddSingleton(sp => new Journal(Path.Combine(DataDir(sp), "journal.bin")));
builder.Services.AddSingleton(sp => new HostStateStore(Path.Combine(DataDir(sp), "state.json")));
builder.Services.AddSingleton<StreamExporter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamExporter>());
builder.Services.AddHostedService<RelayServer>();

var config = builder.Configuration.GetSection(RelayOptions.Section).Get<RelayOptions>() ?? new RelayOptions();
if (Validate(config) is { } error)
{
    Console.Error.WriteLine($"Invalid configuration: {error}");
    return 2;
}

await builder.Build().RunAsync();
return 0;

static string DataDir(IServiceProvider sp)
{
    var dir = Path.GetFullPath(sp.GetRequiredService<IOptions<RelayOptions>>().Value.DataDirectory, AppContext.BaseDirectory);
    Directory.CreateDirectory(dir);
    return dir;
}

static string? Validate(RelayOptions o)
{
    if (o.Port is < 1 or > 65535) return $"port {o.Port}";
    var active = o.Hosts.Where(h => !h.Disabled).ToList();
    if (!active.Any(h => h.Role == HostRole.Source)) return "at least one host with Role = Source is required";
    foreach (var h in active)
    {
        if (string.IsNullOrWhiteSpace(h.Name)) return "a host has no Name";
        if (string.IsNullOrEmpty(h.Label) || Encoding.UTF8.GetByteCount(h.Label) > 16) return $"{h.Name}: Label empty or longer than 16 bytes";
        // Without a password the protocol runs unencrypted and anyone who knows the label can connect
        if (string.IsNullOrEmpty(h.Password)) return $"{h.Name}: Password is required";
    }
    if (active.GroupBy(h => h.Label).Any(g => g.Count() > 1)) return "two hosts with the same Label";
    if (active.GroupBy(h => h.Name).Any(g => g.Count() > 1)) return "two hosts with the same Name";
    return null;
}
