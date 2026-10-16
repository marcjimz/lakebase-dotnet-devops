using Lakebase;
using SampleApp.Data;

EnvConfig.LoadDatabase();

var builder = WebApplication.CreateBuilder(args);
var dbSettings = DatabaseConnectionSettings.Load();

builder.Services.AddLakebaseDbContext<AppDbContext>(dbSettings);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.ReferenceHandler =
        System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapGet("/", () => Results.Redirect("/swagger"));
}

app.MapControllers();
app.Run();

public partial class Program { }
