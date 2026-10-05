using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TodoApi.Data;

/// <summary>
/// Used only by the EF tools (<c>dotnet ef migrations add</c>, <c>database update</c>, <c>migrations bundle</c>).
/// Reads ConnectionStrings__tododb; without it a placeholder lets you scaffold migrations offline.
/// </summary>
public class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__tododb")
                         ?? "Host=localhost;Database=tododb;Username=design;Password=design";
        var options = new DbContextOptionsBuilder<TodoDbContext>().UseNpgsql(connection).Options;
        return new TodoDbContext(options);
    }
}
